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
        private readonly IConfiguration _configuration;
        private readonly IAccountDeletionNotificationService _notificationService;
        private readonly SystemStatusService _systemStatusService;
        private readonly StripeRefundService _refundService;
        private readonly ILoggingService _loggingService;

        // Estados de contratación que requieren atención especial
        private readonly string[] _activeStatuses = { "pending", "awaiting_client_decision", "disputed" };

        public AccountDeletionService(
            AppDbContext context,

            IConfiguration configuration,
            IAccountDeletionNotificationService notificationService,
            SystemStatusService systemStatusService,
            StripeRefundService refundService,
            ILoggingService loggingService)
        {
            _context = context;
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
                // Default to "pending" (ID = 1)
                return 1;
            }
            
            return systemStatus.Id;
        }

        public async Task<AccountDeletionStatusDto> CheckDeletionStatusAsync(int userId)
        {
            try
            {
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
                // 1. Verificar usuario y contraseña
                // ✅ MEJORA: IgnoreQueryFilters() para poder acceder a usuarios eliminados si es necesario
                var user = await _context.Users
                    .IgnoreQueryFilters() // Ignorar query filter para poder acceder a usuarios eliminados
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
                
                // ✅ VALIDACIÓN: Verificar que el usuario no esté ya eliminado
                if (user.IsDeleted)
                {
                    return new AccountDeletionResponseDto
                    {
                        Success = false,
                        Message = $"Usuario ya fue eliminado el {user.DeletedAt:yyyy-MM-dd HH:mm:ss}"
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

                // 6. Enviar notificación al usuario que eliminó su cuenta (antes de eliminarlo)
                await _notificationService.SendAccountDeletionNotificationAsync(userId, request.Reason ?? "Sin razón especificada");

                // 7. Confirmar transacción
                await transaction.CommitAsync();
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
                    
                    // ✅ MEJOR PRÁCTICA: Logging completo del error antes de rethrow
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Account deletion transaction rolled back",
                        details: $"Account deletion transaction for user {userId} was rolled back due to error. Error Type: {ex.GetType().Name}, Error Message: {ex.Message}, Stack Trace: {ex.StackTrace}. " +
                                $"All changes have been rolled back. User account remains intact.",
                        userId: userId,
                        source: "AccountDeletionService.DeleteAccountAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new { 
                            DeletedUserId = userId,
                            ErrorType = ex.GetType().Name,
                            ErrorMessage = ex.Message,
                            StackTrace = ex.StackTrace,
                            InnerException = ex.InnerException?.Message,
                            TransactionRolledBack = true
                        }
                    );
                    
                    throw; // Re-throw para que el controller maneje el error
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
                     // 🚨 VERIFICACIÓN CRÍTICA: No tocar nada si ya está finalizado
                     if (searchHire.Status.IsFinalizationStatus)
                     {
                         continue; // Saltar al siguiente SearchHire - NO tocar nada
                     }

                     // Verificar si hay subestado de finalización en appointment
                     var existingAppointment = await _context.Appointments
                         .Include(a => a.Status)
                         .FirstOrDefaultAsync(a => a.SearchHireId == searchHire.Id);
                     
                     if (existingAppointment?.Status != null && existingAppointment.Status.IsFinalizationStatus)
                     {
                         continue; // Saltar al siguiente SearchHire - NO tocar nada
                     }

                     // Cancelar citas asociadas si existen (solo para SearchHires NO finalizados)
                     var appointmentsToProcess = await _context.Appointments
                         .Where(a => a.SearchHireId == searchHire.Id)
                         .ToListAsync();
                     
                     foreach (var appointment in appointmentsToProcess)
                     {
                         // Usar el estado apropiado según quién elimina la cuenta
                         appointment.StatusId = isClientDeleting ? 13 : 15; // appointment_cancelled_by_client : appointment_cancelled_by_expert
                         appointment.UpdatedAt = DateTime.UtcNow;
                     }

                     // 🎯 PROCESAR DINERO SOLO PARA SEARCHHIRES NO FINALIZADOS
                     // ✅ MEJORAS: Procesar dinero automáticamente a favor del afectado y notificar a ambos
                     if (isClientDeleting)
                     {
                         // Si el cliente elimina su cuenta, dar el dinero al experto
                        var transferSuccess = await _refundService.ProcessMoneyDistributionAsync(
                            searchHire.Id,
                            "cancelled_by_client_account_delete",
                            "Client account deletion - transfer to expert",
                            updateState: true);
                        
                        if (!transferSuccess)
                        {
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Failed to process transfer to expert for account deletion",
                                details: $"Transfer to expert failed for account deletion SearchHire {searchHire.Id}. Amount: {searchHire.Amount}€. Manual intervention required.",
                                userId: searchHire.ExpertId,
                                source: "AccountDeletionService.ProcessActiveContractsAsync",
                                relatedEntityType: "Transfer",
                                relatedEntityId: searchHire.Id,
                                notifyUser: true,
                                additionalData: new { 
                                    SearchHireId = searchHire.Id,
                                    Amount = searchHire.Amount,
                                    ExpertId = searchHire.ExpertId,
                                    DeletionReason = deletionReason
                                }
                            );
                            
                            throw new Exception("Failed to process transfer to expert");
                        }
                        
                        // ✅ Notificar al experto que recibió el pago
                        if (searchHire.ExpertId.HasValue)
                        {
                            await _loggingService.LogInfoAsync(
                                message: "Pago procesado por eliminación de cuenta del cliente",
                                details: $"El cliente del servicio #{searchHire.Id} eliminó su cuenta. Se procesó automáticamente el pago de {searchHire.Amount:F2}€ a tu favor. El dinero está disponible en tu cuenta de Stripe.",
                                userId: searchHire.ExpertId.Value,
                                source: "AccountDeletionService.ProcessActiveContractsAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHire.Id,
                                notifyUser: true,
                                additionalData: new { 
                                    SearchHireId = searchHire.Id,
                                    Amount = searchHire.Amount,
                                    Action = "AccountDeletion_ClientDeleted",
                                    DeletionReason = deletionReason
                                }
                            );
                        }
                        
                        // ✅ Notificar al cliente (antes de que se elimine su cuenta) sobre el procesamiento
                        await _loggingService.LogInfoAsync(
                            message: "Servicio cancelado por eliminación de cuenta",
                            details: $"Al eliminar tu cuenta, el servicio #{searchHire.Id} fue cancelado y el pago de {searchHire.Amount:F2}€ fue transferido automáticamente al experto.",
                            userId: userId,
                            source: "AccountDeletionService.ProcessActiveContractsAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHire.Id,
                            notifyUser: true,
                            additionalData: new { 
                                SearchHireId = searchHire.Id,
                                Amount = searchHire.Amount,
                                Action = "AccountDeletion_ClientDeleted",
                                DeletionReason = deletionReason
                            }
                        );
                        
                         searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Cancelled.ToStringValue());
                         searchHire.UpdatedAt = DateTime.UtcNow;
                     }
                     else
                     {
                         // Si el experto elimina su cuenta, reembolsar al cliente
                        var refundSuccess = await _refundService.ProcessMoneyDistributionAsync(
                            searchHire.Id,
                            "cancelled_by_expert_account_delete",
                            reasonText,
                            updateState: true);
                        
                        if (!refundSuccess)
                        {
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Failed to process Stripe refund for account deletion",
                                details: $"Stripe refund failed for account deletion SearchHire {searchHire.Id}. Amount: {searchHire.Amount}€. Manual intervention required.",
                                userId: searchHire.ClientId,
                                source: "AccountDeletionService.ProcessActiveContractsAsync",
                                relatedEntityType: "Refund",
                                relatedEntityId: searchHire.Id,
                                notifyUser: true,
                                additionalData: new { 
                                    SearchHireId = searchHire.Id,
                                    Amount = searchHire.Amount,
                                    ClientId = searchHire.ClientId,
                                    Reason = "Account deletion",
                                    DeletionReason = deletionReason
                                }
                            );
                            
                            throw new Exception("Failed to process Stripe refund");
                        }
                        
                        // ✅ Notificar al cliente que recibió el reembolso
                        await _loggingService.LogInfoAsync(
                            message: "Reembolso procesado por eliminación de cuenta del experto",
                            details: $"El experto del servicio #{searchHire.Id} eliminó su cuenta. Se procesó automáticamente tu reembolso de {searchHire.Amount:F2}€. El dinero llegará a tu cuenta en 5-10 días hábiles.",
                            userId: searchHire.ClientId,
                            source: "AccountDeletionService.ProcessActiveContractsAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHire.Id,
                            notifyUser: true,
                            additionalData: new { 
                                SearchHireId = searchHire.Id,
                                Amount = searchHire.Amount,
                                Action = "AccountDeletion_ExpertDeleted",
                                DeletionReason = deletionReason
                            }
                        );
                        
                        // ✅ Notificar al experto (antes de que se elimine su cuenta) sobre el procesamiento
                        if (searchHire.ExpertId.HasValue)
                        {
                            await _loggingService.LogInfoAsync(
                                message: "Servicio cancelado por eliminación de cuenta",
                                details: $"Al eliminar tu cuenta, el servicio #{searchHire.Id} fue cancelado y se procesó automáticamente el reembolso de {searchHire.Amount:F2}€ al cliente.",
                                userId: userId,
                                source: "AccountDeletionService.ProcessActiveContractsAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHire.Id,
                                notifyUser: true,
                                additionalData: new { 
                                    SearchHireId = searchHire.Id,
                                    Amount = searchHire.Amount,
                                    Action = "AccountDeletion_ExpertDeleted",
                                    DeletionReason = deletionReason
                                }
                            );
                        }
                        
                         searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Cancelled.ToStringValue());
                         searchHire.UpdatedAt = DateTime.UtcNow;
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


        /// <summary>
        /// Elimina/anonimiza todos los datos de un usuario siguiendo las mejores prácticas del sistema.
        /// Estructura en fases similar a ProcessMoneyDistributionAsync:
        /// - Fase 1: Validaciones (dentro de transacción global)
        /// - Fase 2: Anonimización de datos críticos (misma transacción global - sin nested tx)
        /// - Fase 3: Eliminación de datos no críticos (misma transacción global - batch deletes)
        /// - Fase 4: Eliminación del usuario (misma transacción global)
        /// 
        /// NOTA: Todo se ejecuta en la transacción global de DeleteAccountAsync para evitar problemas de nested transactions.
        /// </summary>
        private async Task DeleteUserDataAsync(int userId)
        {
            // ===== FASE 1: VALIDACIONES (dentro de transacción global) =====
            try
            {
                // ✅ VALIDACIONES PRE-DELETE: Verificar que no haya transacciones pendientes
                var pendingTransactions = await _context.FinancialTransactions
                    .Where(ft => ft.UserId == userId && 
                                (ft.TransactionType == "ServicePayment" || ft.TransactionType == "Deposit"))
                    .AnyAsync();
                
                if (pendingTransactions)
                {
                    await _loggingService.LogWarningAsync(
                        message: "Account deletion attempted with pending financial transactions",
                        details: $"User {userId} has pending financial transactions. Review before deletion.",
                        userId: userId,
                        source: "AccountDeletionService.DeleteUserDataAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId
                    );
                    // Continuar pero loguear para auditoría
                }

                // ===== FASE 2: ANONIMIZACIÓN DE DATOS CRÍTICOS (misma transacción global) =====
                // ✅ MEJOR PRÁCTICA: Todo en la misma transacción global para evitar problemas de nested transactions
                // Usar SQL directo para anonimización (más eficiente y evita problemas de EF Core)
                try
                {
                    // 1. ✅ ANONIMIZAR mensajes (NO ELIMINAR - preservar para la otra parte)
                    // PostgreSQL + C# Best Practice: Anonimizar en lugar de eliminar para preservar contexto
                    // SenderId es nullable, usar NULL directamente
                    // ✅ IDEMPOTENCIA: Solo actualizar si SenderId no es NULL (no anonimizado ya)
                    var messagesCount = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Messages"" 
                          SET ""SenderId"" = NULL, 
                              ""Content"" = '[Usuario eliminado] ' || COALESCE(""Content"", '')
                          WHERE ""SenderId"" = {0} AND ""SenderId"" IS NOT NULL", userId);
                    
                    if (messagesCount > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "Messages anonymized for account deletion",
                            details: $"Anonymized {messagesCount} messages for user {userId}. Context preserved for other party.",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync",
                            relatedEntityType: "Message",
                            relatedEntityId: null
                        );
                    }

                    // 2. ✅ ANONIMIZAR conversaciones (NO ELIMINAR - preservar para la otra parte)
                    // PostgreSQL + C# Best Practice: Anonimizar referencias pero mantener conversación
                    // ClientId y ExpertId son nullable, usar NULL directamente
                    // ✅ IDEMPOTENCIA: Solo actualizar si ClientId/ExpertId no son NULL (no anonimizado ya)
                    var conversationsCount = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Conversations"" 
                          SET ""ClientId"" = CASE WHEN ""ClientId"" = {0} THEN NULL ELSE ""ClientId"" END,
                              ""ExpertId"" = CASE WHEN ""ExpertId"" = {0} THEN NULL ELSE ""ExpertId"" END,
                              ""UpdatedAt"" = CURRENT_TIMESTAMP 
                          WHERE (""ClientId"" = {0} AND ""ClientId"" IS NOT NULL) 
                             OR (""ExpertId"" = {0} AND ""ExpertId"" IS NOT NULL)", userId);
                    
                    if (conversationsCount > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "Conversations anonymized for account deletion",
                            details: $"Anonymized {conversationsCount} conversations for user {userId}. History preserved.",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync",
                            relatedEntityType: "Conversation",
                            relatedEntityId: null
                        );
                    }

                    // 3. ✅ ANONIMIZAR reseñas dadas (NO ELIMINAR - preservar para mantener calificaciones)
                    // PostgreSQL + C# Best Practice: Anonimizar pero preservar rating para mantener promedios
                    // ReviewerId es nullable, usar NULL directamente
                    // ✅ IDEMPOTENCIA: Solo actualizar si ReviewerId no es NULL (no anonimizado ya)
                    // ✅ MEJORA: Agregar UpdatedAt para trazabilidad (aunque Review no tiene UpdatedAt, se preserva CreatedAt)
                    var reviewsCount = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Reviews"" 
                          SET ""ReviewerId"" = NULL, 
                              ""Description"" = CASE WHEN ""Description"" IS NOT NULL AND ""Description"" != '' 
                                  THEN '[Usuario eliminado] ' || ""Description"" 
                                  ELSE ""Description"" END
                          WHERE ""ReviewerId"" = {0} AND ""ReviewerId"" IS NOT NULL", userId);
                    
                    if (reviewsCount > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "Reviews anonymized for account deletion",
                            details: $"Anonymized {reviewsCount} reviews for user {userId}. Ratings and averages preserved.",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync",
                            relatedEntityType: "Review",
                            relatedEntityId: null
                        );
                    }

                    // 4. ✅ ANONIMIZAR transacciones financieras (NO ELIMINAR - requerido por ley)
                    // PostgreSQL + C# Best Practice: Anonimizar datos financieros para cumplimiento legal
                    // Las transacciones financieras deben conservarse por 6 años (España) y para auditoría de Stripe
                    // IMPORTANTE: Anonimizar ANTES de eliminar el usuario para evitar cascade delete
                    // UserId es nullable, usar NULL directamente
                    // ✅ IDEMPOTENCIA: Solo actualizar si UserId no es NULL (no anonimizado ya)
                    // ✅ MEJORA: Agregar UpdatedAt para trazabilidad de cuándo se anonimizó
                    var transactionsCount = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""FinancialTransactions"" 
                          SET ""UserId"" = NULL,
                              ""UpdatedAt"" = CURRENT_TIMESTAMP
                          WHERE ""UserId"" = {0} AND ""UserId"" IS NOT NULL", userId);
                    
                    if (transactionsCount > 0)
                    {
                        // ✅ LOG: Anonimización de transacciones financieras
                        await _loggingService.LogInfoAsync(
                            message: "Financial transactions anonymized for account deletion",
                            details: $"Anonymized {transactionsCount} financial transactions for user {userId}. Financial data preserved for legal compliance (6 years retention - Spain accounting law) and Stripe reconciliation. UserId set to NULL (deleted user marker).",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync",
                            relatedEntityType: "FinancialTransaction",
                            relatedEntityId: null,
                            additionalData: new { 
                                DeletedUserId = userId,
                                TransactionsAnonymized = transactionsCount,
                                Action = "AnonymizeFinancialTransactions",
                                LegalCompliance = "6 years retention (Spain accounting law)",
                                StripeReconciliation = "Preserved StripeRefundId, StripeTransferId, StripePaymentIntentId, Amount, TransactionType, CreatedAt"
                            }
                        );
                    }

                    // 5. ✅ ANONIMIZAR notificaciones (NO ELIMINAR - preservar para auditoría)
                    // PostgreSQL + C# Best Practice: Anonimizar pero preservar para trazabilidad
                    // Notification.UserId es nullable, usar NULL directamente
                    // ✅ IDEMPOTENCIA: Solo actualizar si UserId no es NULL (no anonimizado ya)
                    var notificationsCount = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Notifications"" 
                          SET ""UserId"" = NULL, 
                              ""Message"" = '[Usuario eliminado] ' || COALESCE(""Message"", '')
                          WHERE ""UserId"" = {0} AND ""UserId"" IS NOT NULL", userId);
                    
                    if (notificationsCount > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "Notifications anonymized for account deletion",
                            details: $"Anonymized {notificationsCount} notifications for user {userId}. Audit trail preserved.",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync",
                            relatedEntityType: "Notification",
                            relatedEntityId: null
                        );
                    }

                    // 6. ✅ ANONIMIZAR SearchHires (NO ELIMINAR - preservar contrataciones históricas)
                    // PostgreSQL + C# Best Practice: Anonimizar referencias pero mantener historial de contrataciones
                    // ClientId y ExpertId son ahora nullable, permitiendo anonimización completa
                    // ✅ MEJORA: Anonimización completa de SearchHires (ClientId y ExpertId)
                    var searchHiresAsClient = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""SearchHires"" 
                          SET ""ClientId"" = NULL,
                              ""UpdatedAt"" = CURRENT_TIMESTAMP 
                          WHERE ""ClientId"" = {0} AND ""ClientId"" IS NOT NULL", userId);
                    
                    var searchHiresAsExpert = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""SearchHires"" 
                          SET ""ExpertId"" = NULL,
                              ""UpdatedAt"" = CURRENT_TIMESTAMP 
                          WHERE ""ExpertId"" = {0} AND ""ExpertId"" IS NOT NULL", userId);
                    
                    var totalSearchHiresAnonymized = searchHiresAsClient + searchHiresAsExpert;
                    
                    if (totalSearchHiresAnonymized > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "SearchHires anonymized for account deletion",
                            details: $"Anonymized {totalSearchHiresAnonymized} SearchHires for user {userId} ({searchHiresAsClient} as Client, {searchHiresAsExpert} as Expert). Contract history preserved.",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: null
                        );
                    }
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    // ✅ MEJOR PRÁCTICA: Manejo específico de concurrencia (como ProcessMoneyDistributionAsync)
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Concurrency conflict during account anonymization",
                        details: $"Another process modified user {userId} data concurrently during anonymization. Error: {ex.Message}. " +
                                $"ACTION REQUIRED: Review concurrent operations and retry account deletion if needed.",
                        userId: userId,
                        source: "AccountDeletionService.DeleteUserDataAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new { 
                            DeletedUserId = userId,
                            Error = ex.Message,
                            ErrorType = ex.GetType().Name
                        }
                    );
                    throw; // Re-throw para que la transacción global haga rollback
                }
                catch (Exception ex)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Failed to anonymize user data",
                        details: $"Failed to anonymize critical data for user {userId}. Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                                $"ACTION REQUIRED: Review error and verify data integrity.",
                        userId: userId,
                        source: "AccountDeletionService.DeleteUserDataAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new { 
                            DeletedUserId = userId,
                            ErrorType = ex.GetType().Name,
                            ErrorMessage = ex.Message,
                            StackTrace = ex.StackTrace
                        }
                    );
                    throw; // Re-throw para que la transacción global haga rollback
                }
                
                // ===== FASE 3: ELIMINACIÓN DE DATOS NO CRÍTICOS (misma transacción global - BATCH) =====
                // ✅ MEJORA: Batch deletes - agrupar todos los RemoveRange y un solo SaveChangesAsync
                // Esto mejora performance significativamente al reducir roundtrips a la BD
                
                bool hasDeletes = false;
                
                // 7. Eliminar likes (datos no críticos)
                var likes = await _context.Likes
                    .Where(l => l.UserId == userId)
                    .ToListAsync();
                if (likes.Any())
                {
                    _context.Likes.RemoveRange(likes);
                    hasDeletes = true;
                }

                // 8. Eliminar búsquedas (datos no críticos)
                var searches = await _context.Searches
                    .Where(s => s.UserId == userId)
                    .ToListAsync();
                if (searches.Any())
                {
                    _context.Searches.RemoveRange(searches);
                    hasDeletes = true;
                }

                // 9. Eliminar servicios (si es experto - datos no críticos)
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
                        hasDeletes = true;
                    }

                    // Eliminar servicios
                    if (expertProfile.SearchServices.Any())
                    {
                        _context.SearchServices.RemoveRange(expertProfile.SearchServices);
                        hasDeletes = true;
                    }

                    // Eliminar perfil de experto
                    _context.ExpertProfiles.Remove(expertProfile);
                    hasDeletes = true;
                }

                // 10. Eliminar configuraciones de usuario (datos no críticos)
                var userSettings = await _context.UserSettings
                    .Where(us => us.UserId == userId)
                    .ToListAsync();
                if (userSettings.Any())
                {
                    _context.UserSettings.RemoveRange(userSettings);
                    hasDeletes = true;
                }

                // 11. Eliminar suscripciones (datos no críticos)
                var subscriptions = await _context.UserSubscriptions
                    .Where(us => us.UserId == userId)
                    .ToListAsync();
                if (subscriptions.Any())
                {
                    _context.UserSubscriptions.RemoveRange(subscriptions);
                    hasDeletes = true;
                }
                
                // ✅ BATCH SAVE: Un solo SaveChangesAsync para todos los deletes (mejor performance)
                if (hasDeletes)
                {
                    await _context.SaveChangesAsync();
                }

                // ===== FASE 4: SOFT DELETE DEL USUARIO (misma transacción global) =====
                // ✅ MEJORA: Soft delete en lugar de hard delete para permitir recuperación y cumplimiento legal
                // El query filter en AppDbContext excluirá automáticamente usuarios con IsDeleted = true
                
                // ✅ IDEMPOTENCIA: Verificar que el usuario aún existe y no está eliminado
                // NOTA: Usar IgnoreQueryFilters() para acceder a usuarios eliminados si es necesario
                var userToDelete = await _context.Users
                    .IgnoreQueryFilters() // Ignorar query filter para poder acceder a usuarios eliminados
                    .FirstOrDefaultAsync(u => u.Id == userId);
                
                if (userToDelete != null && !userToDelete.IsDeleted)
                {
                    // ✅ SOFT DELETE: Marcar como eliminado en lugar de remover físicamente
                    userToDelete.IsDeleted = true;
                    userToDelete.DeletedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    
                    await _loggingService.LogInfoAsync(
                        message: "User soft deleted successfully",
                        details: $"User {userId} has been soft deleted (IsDeleted=true, DeletedAt={userToDelete.DeletedAt:O}). User will be excluded from queries automatically by query filter.",
                        userId: null,
                        source: "AccountDeletionService.DeleteUserDataAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId
                    );
                }
                else if (userToDelete != null && userToDelete.IsDeleted)
                {
                    // Usuario ya fue eliminado (idempotencia)
                    await _loggingService.LogWarningAsync(
                        message: "User already soft deleted - idempotent call",
                        details: $"User {userId} was already soft deleted at {userToDelete.DeletedAt:O}. Account deletion process completed (idempotent).",
                        userId: null,
                        source: "AccountDeletionService.DeleteUserDataAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId
                    );
                }
                else
                {
                    // Usuario no existe
                    await _loggingService.LogWarningAsync(
                        message: "User not found for deletion",
                        details: $"User {userId} not found. Account deletion process completed (idempotent).",
                        userId: null,
                        source: "AccountDeletionService.DeleteUserDataAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId
                    );
                }
                
                // ✅ LOG FINAL: Eliminación de datos completada
                await _loggingService.LogInfoAsync(
                    message: "User data deletion completed successfully",
                    details: $"All user data for user {userId} has been anonymized or deleted. Account deletion process completed.",
                    userId: null,
                    source: "AccountDeletionService.DeleteUserDataAsync",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        DeletedUserId = userId,
                        Action = "AccountDeletionCompleted",
                        Timestamp = DateTime.UtcNow
                    }
                );
            }
            catch (Exception ex)
            {
                // ✅ MEJOR PRÁCTICA: Logging completo del error antes de rethrow
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Account deletion failed",
                    details: $"Failed to delete user data for user {userId}. Error Type: {ex.GetType().Name}, Error Message: {ex.Message}, Stack Trace: {ex.StackTrace}. " +
                            $"ACTION REQUIRED: Review error and verify data integrity. User may be in inconsistent state.",
                    userId: userId,
                    source: "AccountDeletionService.DeleteUserDataAsync",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        DeletedUserId = userId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );
                throw; // Re-throw para que la transacción global haga rollback
            }
        }

    }
}
