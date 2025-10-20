using Microsoft.EntityFrameworkCore;
using newApi.DataLayer;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using System.Security.Cryptography;
using System.Text;
using Stripe;
using newApi.Common;

namespace newApi.Services
{
    public interface IAccountDeletionService
    {
        Task<AccountDeletionStatusDto> CheckDeletionStatusAsync(int userId);
        Task<AccountDeletionResponseDto> DeleteAccountAsync(int userId, AccountDeletionRequestDto request);
    }

    public class AccountDeletionService : IAccountDeletionService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<AccountDeletionService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IAccountDeletionNotificationService _notificationService;
        private readonly SystemStatusService _systemStatusService;
        private readonly StripeRefundService _refundService;
        private readonly ILoggingService _loggingService;

        // Estados de contratación que requieren atención especial
        private readonly string[] _activeStatuses = { "pending", "awaiting_client_decision", "disputed" };

        public AccountDeletionService(
            AppDbContext context,
            ILogger<AccountDeletionService> logger,
            IConfiguration configuration,
            IAccountDeletionNotificationService notificationService,
            SystemStatusService systemStatusService,
            StripeRefundService refundService,
            ILoggingService loggingService)
        {
            _context = context;
            _logger = logger;
            _configuration = configuration;
            _notificationService = notificationService;
            _systemStatusService = systemStatusService;
            _refundService = refundService;
            _loggingService = loggingService;
        }

        /// <summary>
        /// Helper method to get StatusId from StatusValue
        /// </summary>
        private async Task<int> GetStatusIdByValueAsync(string statusValue)
        {
            var systemStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.StatusType == "SearchHireStatus");
            
            if (systemStatus == null)
            {
                _logger.LogWarning("SystemStatus not found for StatusValue: {StatusValue}", statusValue);
                // Default to "pending" (ID = 1)
                return 1;
            }
            
            return systemStatus.Id;
        }

        public async Task<AccountDeletionStatusDto> CheckDeletionStatusAsync(int userId)
        {
            try
            {
                _logger.LogInformation("Checking deletion status for user {UserId}", userId);

                var user = await _context.Users
                    .Include(u => u.SearchHiresAsClient)
                    .Include(u => u.SearchHiresAsExpert)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user == null)
                {
                    return new AccountDeletionStatusDto
                    {
                        CanDeleteImmediately = false,
                        HasActiveContracts = false,
                        ActiveContractsCount = 0,
                        Message = "Usuario no encontrado"
                    };
                }

                // Buscar contrataciones activas
                var activeContracts = await GetActiveContractsAsync(userId);

                var canDeleteImmediately = !activeContracts.Any();
                var message = canDeleteImmediately 
                    ? "La cuenta puede ser eliminada inmediatamente"
                    : $"Se encontraron {activeContracts.Count} contrataciones activas que requieren atención";

                return new AccountDeletionStatusDto
                {
                    CanDeleteImmediately = canDeleteImmediately,
                    HasActiveContracts = activeContracts.Any(),
                    ActiveContractsCount = activeContracts.Count,
                    ActiveContracts = activeContracts,
                    Message = message
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking deletion status for user {UserId}", userId);
                throw;
            }
        }

        public async Task<AccountDeletionResponseDto> DeleteAccountAsync(int userId, AccountDeletionRequestDto request)
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                using var transaction = await _context.Database.BeginTransactionAsync();
                try
            {
                _logger.LogInformation("Starting account deletion process for user {UserId}", userId);

                // 1. Verificar usuario y contraseña
                var user = await _context.Users
                    .Include(u => u.ExpertProfile)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user == null)
                {
                    return new AccountDeletionResponseDto
                    {
                        Success = false,
                        Message = "Usuario no encontrado"
                    };
                }

                // No se requiere verificación de contraseña ya que el sistema solo usa autenticación con Google

                // 2. Obtener contrataciones activas
                var activeContracts = await GetActiveContractsAsync(userId);
                var disputesCreated = new List<DisputeCreatedInfo>();

                // 3. Procesar contrataciones activas
                if (activeContracts.Any())
                {
                    disputesCreated = await ProcessActiveContractsAsync(userId, activeContracts, request.Reason);
                }

                // 4. Eliminar datos del usuario
                await DeleteUserDataAsync(userId);

                // 5. Enviar notificaciones
                if (disputesCreated.Any())
                {
                    await _notificationService.NotifyAffectedUsersAsync(disputesCreated);
                }

                // 6. Enviar notificación al usuario que eliminó su cuenta
                await _notificationService.SendAccountDeletionNotificationAsync(userId, request.Reason);

                // 7. Confirmar transacción
                await transaction.CommitAsync();

                _logger.LogInformation("Successfully deleted account for user {UserId}", userId);

                return new AccountDeletionResponseDto
                {
                    Success = true,
                    Message = activeContracts.Any() 
                        ? $"Cuenta eliminada. Se procesaron {disputesCreated.Count} transacciones automáticas para contrataciones activas."
                        : "Cuenta eliminada exitosamente",
                    ActiveContracts = activeContracts,
                    DisputesCreated = disputesCreated,
                    RequiresManualReview = false // Ya no requiere revisión manual ya que se procesan automáticamente
                };
            }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Error deleting account for user {UserId}", userId);
                    throw;
                }
            });
        }

        private async Task<List<ActiveContractInfo>> GetActiveContractsAsync(int userId)
        {
            var activeContracts = new List<ActiveContractInfo>();

            // Buscar como cliente
            var clientContracts = await _context.SearchHires
                .Where(sh => sh.ClientId == userId && _activeStatuses.Contains(sh.Status.StatusValue))
                .Include(sh => sh.Status)
                .Include(sh => sh.Expert)
                .Include(sh => sh.SearchService)
                    .ThenInclude(ss => ss.ServiceType)
                .Include(sh => sh.Appointment)
                .ToListAsync();

            foreach (var contract in clientContracts)
            {
                activeContracts.Add(new ActiveContractInfo
                {
                    SearchHireId = contract.Id,
                    Status = contract.Status.StatusValue,
                    ServiceName = contract.SearchService.ServiceType?.Name ?? "Servicio",
                    Amount = contract.Amount,
                    CreatedAt = contract.CreatedAt,
                    OtherPartyName = contract.Expert?.Name ?? "Experto",
                    OtherPartyEmail = contract.Expert?.Email ?? "",
                    HasAppointment = contract.Appointment != null,
                    AppointmentDate = contract.Appointment?.ProposedDate
                });
            }

            // Buscar como experto
            var expertContracts = await _context.SearchHires
                .Where(sh => sh.ExpertId == userId && _activeStatuses.Contains(sh.Status.StatusValue))
                .Include(sh => sh.Status)
                .Include(sh => sh.Client)
                .Include(sh => sh.SearchService)
                    .ThenInclude(ss => ss.ServiceType)
                .Include(sh => sh.Appointment)
                .ToListAsync();

            foreach (var contract in expertContracts)
            {
                activeContracts.Add(new ActiveContractInfo
                {
                    SearchHireId = contract.Id,
                    Status = contract.Status.StatusValue,
                    ServiceName = contract.SearchService.ServiceType?.Name ?? "Servicio",
                    Amount = contract.Amount,
                    CreatedAt = contract.CreatedAt,
                    OtherPartyName = contract.Client?.Name ?? "Cliente",
                    OtherPartyEmail = contract.Client?.Email ?? "",
                    HasAppointment = contract.Appointment != null,
                    AppointmentDate = contract.Appointment?.ProposedDate
                });
            }

            return activeContracts;
        }

        private async Task<List<DisputeCreatedInfo>> ProcessActiveContractsAsync(
            int userId, 
            List<ActiveContractInfo> activeContracts, 
            string? deletionReason)
        {
            var transactionsProcessed = new List<DisputeCreatedInfo>();

            foreach (var contract in activeContracts)
            {
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .FirstOrDefaultAsync(sh => sh.Id == contract.SearchHireId);

                if (searchHire == null) continue;

                // Determinar quién es la parte afectada
                var affectedParty = searchHire.ClientId == userId ? searchHire.Expert : searchHire.Client;
                var isClientDeleting = searchHire.ClientId == userId;

                var reasonText = !string.IsNullOrEmpty(deletionReason) 
                    ? (isClientDeleting 
                        ? $"Cliente eliminó su cuenta. Razón: {deletionReason}"
                        : $"Experto eliminó su cuenta. Razón: {deletionReason}")
                    : (isClientDeleting 
                        ? "Cliente eliminó su cuenta"
                        : "Experto eliminó su cuenta");

                 try
                 {
                     // Cancelar citas asociadas si existen
                     var appointments = await _context.Appointments
                         .Where(a => a.SearchHireId == searchHire.Id)
                         .ToListAsync();
                     
                     foreach (var appointment in appointments)
                     {
                         // Usar el estado apropiado según quién elimina la cuenta
                         appointment.StatusId = isClientDeleting ? 13 : 15; // appointment_cancelled_by_client : appointment_cancelled_by_expert
                         appointment.UpdatedAt = DateTime.UtcNow;
                         _logger.LogInformation("Cancelled appointment {AppointmentId} due to account deletion", appointment.Id);
                     }

                     // 🎯 LÓGICA INTELIGENTE: Determinar configuración según si hay subestado de appointment DE FINALIZACIÓN
                     string configStatus;
                     
                     // Verificar si hay una cita asociada con subestado específico DE FINALIZACIÓN
                     var existingAppointment = await _context.Appointments
                         .Include(a => a.Status)
                         .FirstOrDefaultAsync(a => a.SearchHireId == searchHire.Id);
                     
                     if (existingAppointment?.Status != null && existingAppointment.Status.IsFinalizationStatus)
                     {
                         // CASO 1: Hay subestado de appointment DE FINALIZACIÓN - usar configuración específica del subestado
                         configStatus = existingAppointment.Status.StatusValue;
                         _logger.LogInformation("Using appointment sub-status configuration: {ConfigStatus} for SearchHire {SearchHireId}", 
                             configStatus, searchHire.Id);
                     }
                     else
                     {
                         // CASO 2: No hay subestado de appointment o no es de finalización - usar configuración del estado final
                         configStatus = "cancelled";
                         _logger.LogInformation("No appointment sub-status found or not finalization, using final status configuration: {ConfigStatus} for SearchHire {SearchHireId}", 
                             configStatus, searchHire.Id);
                     }
                     
                     var moneyConfig = await _systemStatusService.GetMoneyDistributionConfigAsync(configStatus, 
                         searchHire.SearchService?.CategoryId, 
                         searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);

                     if (isClientDeleting)
                     {
                         // Si el cliente elimina su cuenta, dar el dinero al experto
                         await ProcessTransferToExpertAsync(searchHire.Id, moneyConfig);
                         searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Cancelled.ToStringValue());
                         searchHire.UpdatedAt = DateTime.UtcNow;
                         
                         _logger.LogInformation("Processed transfer to expert for SearchHire {SearchHireId} due to client account deletion with config: {Config}", 
                             searchHire.Id, moneyConfig?.Source ?? "default");
                     }
                     else
                     {
                         // Si el experto elimina su cuenta, reembolsar al cliente
                         await ProcessClientRefundAsync(searchHire.Id, reasonText, moneyConfig);
                         searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Cancelled.ToStringValue());
                         searchHire.UpdatedAt = DateTime.UtcNow;
                         
                         _logger.LogInformation("Processed client refund for SearchHire {SearchHireId} due to expert account deletion with config: {Config}", 
                             searchHire.Id, moneyConfig?.Source ?? "default");
                     }

                    transactionsProcessed.Add(new DisputeCreatedInfo
                    {
                        DisputeId = 0, // No hay disputa, es una transacción directa
                        SearchHireId = searchHire.Id,
                        Reason = reasonText,
                        AffectedPartyName = affectedParty?.Name ?? "Usuario",
                        AffectedPartyEmail = affectedParty?.Email ?? ""
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing contract {SearchHireId} during account deletion: {ErrorMessage}", 
                        searchHire.Id, ex.Message);
                    
                    // Si falla el procesamiento, crear una disputa como fallback
                    var dispute = new newApi.DataLayer.Models.PostGresModels.Dispute
                    {
                        SearchHireId = searchHire.Id,
                        ReporterId = userId,
                        Reason = $"{reasonText} - Error en procesamiento automático: {ex.Message}",
                        Status = "pending",
                        ResolutionComments = "Disputa creada automáticamente por error en eliminación de cuenta",
                        CreatedAt = DateTime.UtcNow,
                        ExpertResponseDeadline = DateTime.UtcNow.AddHours(48)
                    };

                    _context.Disputes.Add(dispute);
                    searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Disputed.ToStringValue());
                    searchHire.UpdatedAt = DateTime.UtcNow;

                    transactionsProcessed.Add(new DisputeCreatedInfo
                    {
                        DisputeId = dispute.Id,
                        SearchHireId = searchHire.Id,
                        Reason = dispute.Reason,
                        AffectedPartyName = affectedParty?.Name ?? "Usuario",
                        AffectedPartyEmail = affectedParty?.Email ?? ""
                    });
                }
            }

            await _context.SaveChangesAsync();
            return transactionsProcessed;
        }

         private async Task ProcessClientRefundAsync(int searchHireId, string reason, MoneyDistributionConfigDto? moneyConfig = null)
        {
            _logger.LogInformation("Processing client refund for searchHireId={SearchHireId}, reason={Reason}", searchHireId, reason);

            try
            {
                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var searchHire = await _context.SearchHires
                    .FromSqlRaw("SELECT * FROM \"SearchHires\" WHERE \"Id\" = {0} FOR UPDATE", searchHireId)
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", searchHireId);
                    throw new Exception("SearchHire not found");
                }

                // Verificar que el servicio esté en estado activo
                if (searchHire.Status.StatusValue != "pending" && searchHire.Status.StatusValue != "awaiting_client_decision")
                {
                    _logger.LogWarning("SearchHire is not in active status for searchHireId={SearchHireId}, current status={Status}", 
                        searchHireId, searchHire.Status);
                    throw new Exception($"SearchHire is not in active status: {searchHire.Status}");
                }

                 // Orquestar refund+transfer según configuración del estado de cancelación por eliminación
                 var refundSuccess = await _refundService.ProcessMoneyDistributionAsync(
                     searchHire.Id,
                     "appointment_cancelled_by_expert",
                     reason);
                 
                 if (!refundSuccess)
                 {
                     _logger.LogError("Failed to process Stripe refund for account deletion searchHireId={SearchHireId}", searchHire.Id);
                     
                     // Log critical error for money transaction failure
                     await _loggingService.LogCriticalAsync(
                         message: "CRITICAL: Failed to process Stripe refund for account deletion",
                         details: $"Stripe refund failed for account deletion SearchHire {searchHire.Id}",
                         userId: searchHire.ClientId,
                         source: "AccountDeletionService.ProcessClientRefundAsync",
                         relatedEntityType: "Refund",
                         relatedEntityId: searchHire.Id,
                         additionalData: new { 
                             SearchHireId = searchHire.Id,
                             Amount = searchHire.Amount,
                             ClientId = searchHire.ClientId,
                             Reason = "Account deletion"
                         }
                     );
                     
                     throw new Exception("Failed to process Stripe refund");
                 }

                 // Actualizar estado del servicio
                 searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Cancelled.ToStringValue());
                 searchHire.UpdatedAt = DateTime.UtcNow;
                 
                 await _context.SaveChangesAsync();

                 _logger.LogInformation("Successfully processed real Stripe refund for account deletion searchHireId={SearchHireId}, reason={Reason}",
                     searchHire.Id, reason);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing client refund for searchHireId={SearchHireId}: {ErrorMessage}", 
                    searchHireId, ex.Message);
                
                // Log critical error for money transaction failure
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Error processing client refund for account deletion",
                    details: ex.ToString(),
                    source: "AccountDeletionService.ProcessClientRefundAsync",
                    relatedEntityType: "Refund",
                    relatedEntityId: searchHireId,
                    additionalData: new { 
                        SearchHireId = searchHireId,
                        ErrorMessage = ex.Message
                    }
                );
                
                throw;
            }
        }

         private async Task ProcessTransferToExpertAsync(int searchHireId, MoneyDistributionConfigDto? moneyConfig = null)
        {
            _logger.LogInformation("Processing transfer to expert for searchHireId={SearchHireId}", searchHireId);

            // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
            var searchHire = await _context.SearchHires
                .FromSqlRaw("SELECT * FROM \"SearchHires\" WHERE \"Id\" = {0} FOR UPDATE", searchHireId)
                .Include(sh => sh.Status)
                .Include(sh => sh.Expert)
                .ThenInclude(e => e.ExpertProfile)
                .Include(sh => sh.SearchService)
                .ThenInclude(ss => ss.ServiceType)
                .FirstOrDefaultAsync();

            if (searchHire == null)
            {
                _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", searchHireId);
                throw new Exception("SearchHire not found");
            }

            // Verificar que el servicio esté en estado válido para transferencia
            if (searchHire.Status.StatusValue != "pending" && searchHire.Status.StatusValue != "awaiting_client_decision")
            {
                _logger.LogWarning("SearchHire is not in valid status for transfer for searchHireId={SearchHireId}, current status={Status}", 
                    searchHireId, searchHire.Status);
                throw new Exception($"SearchHire is not in valid status for transfer: {searchHire.Status}");
            }

            // 🚨 PROTECCIÓN CONTRA TRANSFERENCIAS DUPLICADAS
            if (!string.IsNullOrEmpty(searchHire.ExpertTransferId))
            {
                _logger.LogWarning("Transfer already exists for searchHireId={SearchHireId}, transferId={TransferId}", 
                    searchHireId, searchHire.ExpertTransferId);
                throw new Exception($"Transfer already exists for this SearchHire: {searchHire.ExpertTransferId}");
            }

             // Usar configuración de distribución de dinero pasada como parámetro o obtener una por defecto
             var config = moneyConfig ?? await _systemStatusService.GetMoneyDistributionConfigAsync("completed", 
                 searchHire.SearchService?.CategoryId, 
                 searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);
             
             if (config == null)
             {
                 _logger.LogError("No money distribution configuration found for searchHireId={SearchHireId}", searchHireId);
                 throw new Exception("No money distribution configuration found");
             }
             
             var amountToExpert = searchHire.Amount * (config.ExpertPercentage / 100);
             var amountInCents = (long)(amountToExpert * 100);
             
             _logger.LogInformation("Using money distribution config: Expert={ExpertPercentage}%, Platform={PlatformPercentage}%, Source={Source} for searchHireId={SearchHireId}", 
                 config.ExpertPercentage, config.PlatformPercentage, config.Source, searchHireId);

            var expertStripeAccountId = searchHire.Expert.ExpertProfile?.StripeAccountId;
            if (string.IsNullOrEmpty(expertStripeAccountId))
            {
                _logger.LogError("Expert has no Stripe account for searchHireId={SearchHireId}, expertId={ExpertId}", searchHireId, searchHire.ExpertId);
                
                // Log critical error for money transaction failure
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Expert has no Stripe account for transfer",
                    details: $"Expert {searchHire.ExpertId} has no Stripe account configured for transfer",
                    userId: searchHire.ExpertId,
                    source: "AccountDeletionService.ProcessTransferToExpertAsync",
                    relatedEntityType: "Transfer",
                    relatedEntityId: searchHireId,
                    additionalData: new { 
                        SearchHireId = searchHireId,
                        Amount = searchHire.Amount,
                        ExpertId = searchHire.ExpertId
                    }
                );
                
                throw new Exception("Expert has no Stripe account configured");
            }

            try
            {
                var transferOptions = new TransferCreateOptions
                {
                    Amount = amountInCents,
                    Currency = "eur",
                    Destination = expertStripeAccountId,
                    Metadata = new Dictionary<string, string>
                    {
                        { "searchHireId", searchHireId.ToString() }
                    }
                };

                var transferService = new TransferService();
                var transfer = await transferService.CreateAsync(transferOptions);
                searchHire.ExpertTransferId = transfer.Id;
                
                _logger.LogInformation("Transfer created for searchHireId={SearchHireId}, transferId={TransferId}, amount={Amount}", searchHireId, transfer.Id, amountToExpert);

                // Crear transacción financiera para el pago al experto
                var expertTransaction = new FinancialTransaction
                {
                    UserId = searchHire.ExpertId.Value,
                    Amount = amountToExpert,
                    TransactionType = "Payout",
                    RelatedEntityType = "SearchHire",
                    RelatedEntityId = searchHireId,
                    CreatedAt = DateTime.UtcNow
                };
                _context.FinancialTransactions.Add(expertTransaction);

                await _context.SaveChangesAsync();
                
                _logger.LogInformation("Successfully processed transfer to expert for searchHireId={SearchHireId}, amount={Amount}", 
                    searchHireId, amountToExpert);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Stripe error processing transfer for searchHireId={SearchHireId}: {ErrorMessage}", 
                    searchHireId, ex.Message);
                
                // Log critical error for money transaction failure
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Stripe transfer error for account deletion",
                    details: ex.ToString(),
                    userId: searchHire?.ExpertId,
                    source: "AccountDeletionService.ProcessTransferToExpertAsync",
                    relatedEntityType: "Transfer",
                    relatedEntityId: searchHireId,
                    additionalData: new { 
                        SearchHireId = searchHireId,
                        Amount = searchHire?.Amount,
                        ExpertId = searchHire?.ExpertId,
                        StripeError = ex.Message,
                        StripeErrorType = ex.StripeError?.Type,
                        StripeErrorCode = ex.StripeError?.Code
                    }
                );
                
                throw new Exception($"Stripe transfer failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing transfer for searchHireId={SearchHireId}: {ErrorMessage}", 
                    searchHireId, ex.Message);
                
                // Log critical error for money transaction failure
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Error processing transfer for account deletion",
                    details: ex.ToString(),
                    userId: searchHire?.ExpertId,
                    source: "AccountDeletionService.ProcessTransferToExpertAsync",
                    relatedEntityType: "Transfer",
                    relatedEntityId: searchHireId,
                    additionalData: new { 
                        SearchHireId = searchHireId,
                        Amount = searchHire?.Amount,
                        ExpertId = searchHire?.ExpertId,
                        ErrorMessage = ex.Message
                    }
                );
                
                throw;
            }
        }


        private async Task DeleteUserDataAsync(int userId)
        {
            _logger.LogInformation("Deleting user data for user {UserId}", userId);

            try
            {
                // 1. Eliminar mensajes
                var messages = await _context.Messages
                    .Where(m => m.SenderId == userId)
                    .ToListAsync();
                if (messages.Any())
                {
                    _context.Messages.RemoveRange(messages);
                    await _context.SaveChangesAsync();
                }

                // 2. Eliminar conversaciones
                var conversations = await _context.Conversations
                    .Where(c => c.ClientId == userId || c.ExpertId == userId)
                    .ToListAsync();
                if (conversations.Any())
                {
                    _context.Conversations.RemoveRange(conversations);
                    await _context.SaveChangesAsync();
                }

                // 3. Eliminar likes
                var likes = await _context.Likes
                    .Where(l => l.UserId == userId)
                    .ToListAsync();
                if (likes.Any())
                {
                    _context.Likes.RemoveRange(likes);
                    await _context.SaveChangesAsync();
                }

                // 4. Eliminar reseñas dadas
                var reviewsGiven = await _context.Reviews
                    .Where(r => r.ReviewerId == userId)
                    .ToListAsync();
                if (reviewsGiven.Any())
                {
                    _context.Reviews.RemoveRange(reviewsGiven);
                    await _context.SaveChangesAsync();
                }

                // 5. Eliminar búsquedas
                var searches = await _context.Searches
                    .Where(s => s.UserId == userId)
                    .ToListAsync();
                if (searches.Any())
                {
                    _context.Searches.RemoveRange(searches);
                    await _context.SaveChangesAsync();
                }

                // 6. Eliminar servicios (si es experto)
                var expertProfile = await _context.ExpertProfiles
                    .Include(ep => ep.SearchServices)
                        .ThenInclude(ss => ss.Images)
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile != null)
                {
                    // Eliminar imágenes de servicios
                    var serviceImages = expertProfile.SearchServices
                        .SelectMany(ss => ss.Images)
                        .ToList();
                    if (serviceImages.Any())
                    {
                        _context.SearchServiceImages.RemoveRange(serviceImages);
                        await _context.SaveChangesAsync();
                    }

                    // Eliminar servicios
                    if (expertProfile.SearchServices.Any())
                    {
                        _context.SearchServices.RemoveRange(expertProfile.SearchServices);
                        await _context.SaveChangesAsync();
                    }

                    // Eliminar perfil de experto
                    _context.ExpertProfiles.Remove(expertProfile);
                    await _context.SaveChangesAsync();
                }

                // 7. Eliminar configuraciones de usuario
                var userSettings = await _context.UserSettings
                    .Where(us => us.UserId == userId)
                    .ToListAsync();
                if (userSettings.Any())
                {
                    _context.UserSettings.RemoveRange(userSettings);
                    await _context.SaveChangesAsync();
                }

                // 8. Eliminar suscripciones
                var subscriptions = await _context.UserSubscriptions
                    .Where(us => us.UserId == userId)
                    .ToListAsync();
                if (subscriptions.Any())
                {
                    _context.UserSubscriptions.RemoveRange(subscriptions);
                    await _context.SaveChangesAsync();
                }

                // 9. Eliminar transacciones financieras
                var transactions = await _context.FinancialTransactions
                    .Where(ft => ft.UserId == userId)
                    .ToListAsync();
                if (transactions.Any())
                {
                    _context.FinancialTransactions.RemoveRange(transactions);
                    await _context.SaveChangesAsync();
                }

                // 10. Eliminar notificaciones
                var notifications = await _context.Notifications
                    .Where(n => n.UserId == userId)
                    .ToListAsync();
                if (notifications.Any())
                {
                    _context.Notifications.RemoveRange(notifications);
                    await _context.SaveChangesAsync();
                }

                // 11. Finalmente, eliminar el usuario
                var user = await _context.Users.FindAsync(userId);
                if (user != null)
                {
                    _context.Users.Remove(user);
                    await _context.SaveChangesAsync();
                }

                _logger.LogInformation("Successfully deleted all user data for user {UserId}", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting user data for user {UserId}", userId);
                throw;
            }
        }

    }
}
