using Microsoft.EntityFrameworkCore;
using newApi.DataLayer;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using System.Security.Cryptography;
using System.Text;

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

        // Estados de contratación que requieren atención especial
        private readonly string[] _activeStatuses = { "pending", "awaiting_client_decision", "disputed" };

        public AccountDeletionService(
            AppDbContext context,
            ILogger<AccountDeletionService> logger,
            IConfiguration configuration,
            IAccountDeletionNotificationService notificationService)
        {
            _context = context;
            _logger = logger;
            _configuration = configuration;
            _notificationService = notificationService;
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
                        ? $"Cuenta eliminada. Se crearon {disputesCreated.Count} disputas para contrataciones activas."
                        : "Cuenta eliminada exitosamente",
                    ActiveContracts = activeContracts,
                    DisputesCreated = disputesCreated,
                    RequiresManualReview = activeContracts.Any()
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
                .Where(sh => sh.ClientId == userId && _activeStatuses.Contains(sh.Status))
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
                    Status = contract.Status,
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
                .Where(sh => sh.ExpertId == userId && _activeStatuses.Contains(sh.Status))
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
                    Status = contract.Status,
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
            string deletionReason)
        {
            var disputesCreated = new List<DisputeCreatedInfo>();

            foreach (var contract in activeContracts)
            {
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .FirstOrDefaultAsync(sh => sh.Id == contract.SearchHireId);

                if (searchHire == null) continue;

                // Determinar quién es la parte afectada
                var affectedParty = searchHire.ClientId == userId ? searchHire.Expert : searchHire.Client;
                var isClientDeleting = searchHire.ClientId == userId;

                // Crear disputa automática
                var reasonText = !string.IsNullOrEmpty(deletionReason) 
                    ? (isClientDeleting 
                        ? $"Cliente eliminó su cuenta. Razón: {deletionReason}"
                        : $"Experto eliminó su cuenta. Razón: {deletionReason}")
                    : (isClientDeleting 
                        ? "Cliente eliminó su cuenta"
                        : "Experto eliminó su cuenta");
                        
                var dispute = new Dispute
                {
                    SearchHireId = searchHire.Id,
                    ReporterId = userId,
                    Reason = reasonText,
                    Status = "pending",
                    ResolutionComments = "Disputa creada automáticamente por eliminación de cuenta",
                    CreatedAt = DateTime.UtcNow,
                    ExpertResponseDeadline = DateTime.UtcNow.AddHours(48) // 48h para responder
                };

                _context.Disputes.Add(dispute);
                await _context.SaveChangesAsync();

                // Actualizar estado de la contratación
                searchHire.Status = "disputed";
                searchHire.UpdatedAt = DateTime.UtcNow;

                disputesCreated.Add(new DisputeCreatedInfo
                {
                    DisputeId = dispute.Id,
                    SearchHireId = searchHire.Id,
                    Reason = dispute.Reason,
                    AffectedPartyName = affectedParty?.Name ?? "Usuario",
                    AffectedPartyEmail = affectedParty?.Email ?? ""
                });

                _logger.LogInformation("Created automatic dispute {DisputeId} for SearchHire {SearchHireId} due to account deletion", 
                    dispute.Id, searchHire.Id);
            }

            await _context.SaveChangesAsync();
            return disputesCreated;
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
