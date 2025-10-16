using Microsoft.EntityFrameworkCore;
using newApi.DataLayer;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;

namespace newApi.Services
{
    public interface IAccountDeletionNotificationService
    {
        Task NotifyAffectedUsersAsync(List<DisputeCreatedInfo> disputesCreated);
        Task SendAccountDeletionNotificationAsync(int userId, string reason);
    }

    public class AccountDeletionNotificationService : IAccountDeletionNotificationService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<AccountDeletionNotificationService> _logger;

        public AccountDeletionNotificationService(
            AppDbContext context,
            ILogger<AccountDeletionNotificationService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task NotifyAffectedUsersAsync(List<DisputeCreatedInfo> disputesCreated)
        {
            try
            {
                _logger.LogInformation("Sending notifications to {Count} affected users", disputesCreated.Count);

                foreach (var dispute in disputesCreated)
                {
                    await SendDisputeNotificationAsync(dispute);
                }

                _logger.LogInformation("Successfully sent notifications to all affected users");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending notifications to affected users");
                throw;
            }
        }

        public async Task SendAccountDeletionNotificationAsync(int userId, string? reason)
        {
            try
            {
                var user = await _context.Users.FindAsync(userId);
                if (user == null)
                {
                    _logger.LogWarning("User {UserId} not found for deletion notification", userId);
                    return;
                }

                var message = !string.IsNullOrEmpty(reason) 
                    ? $"Tu cuenta ha sido eliminada exitosamente. Razón: {reason}"
                    : "Tu cuenta ha sido eliminada exitosamente";
                    
                var notification = new Notification
                {
                    UserId = userId,
                    Title = "Cuenta Eliminada",
                    Message = message,
                    Type = "account_deletion",
                    Read = false,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Notifications.Add(notification);
                await _context.SaveChangesAsync();

                // Notificación guardada en base de datos

                _logger.LogInformation("Sent account deletion notification to user {UserId}", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending account deletion notification to user {UserId}", userId);
                throw;
            }
        }

        private async Task SendDisputeNotificationAsync(DisputeCreatedInfo dispute)
        {
            try
            {
                // Buscar el usuario afectado por la disputa
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                    .FirstOrDefaultAsync(sh => sh.Id == dispute.SearchHireId);

                if (searchHire == null)
                {
                    _logger.LogWarning("SearchHire {SearchHireId} not found for dispute notification", dispute.SearchHireId);
                    return;
                }

                // Determinar quién es el usuario afectado (el que NO eliminó la cuenta)
                var affectedUser = searchHire.Client?.Email == dispute.AffectedPartyEmail 
                    ? searchHire.Client 
                    : searchHire.Expert;

                if (affectedUser == null)
                {
                    _logger.LogWarning("Affected user not found for SearchHire {SearchHireId}", dispute.SearchHireId);
                    return;
                }

                var serviceName = searchHire.SearchService?.ServiceType?.Name ?? "Servicio";
                var isClientAffected = searchHire.ClientId == affectedUser.Id;

                var notification = new Notification
                {
                    UserId = affectedUser.Id,
                    Title = "Contratación en Disputa - Cuenta Eliminada",
                    Message = isClientAffected
                        ? $"El experto del servicio '{serviceName}' ha eliminado su cuenta. Se ha creado una disputa automática para proteger tus intereses. Tienes 48 horas para responder."
                        : $"El cliente del servicio '{serviceName}' ha eliminado su cuenta. Se ha creado una disputa automática. Tienes 48 horas para responder.",
                    Type = "dispute_created",
                    Read = false,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Notifications.Add(notification);
                await _context.SaveChangesAsync();

                // Notificación guardada en base de datos

                _logger.LogInformation("Sent dispute notification to affected user {UserId} for SearchHire {SearchHireId}", 
                    affectedUser.Id, dispute.SearchHireId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending dispute notification for SearchHire {SearchHireId}", dispute.SearchHireId);
                throw;
            }
        }
    }
}
