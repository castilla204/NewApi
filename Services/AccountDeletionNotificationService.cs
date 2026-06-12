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
        private readonly IInAppNotificationService _inAppNotifications;
        public AccountDeletionNotificationService(AppDbContext context, IInAppNotificationService inAppNotifications)
        {
            _context = context;
            _inAppNotifications = inAppNotifications;
        }

        public async Task NotifyAffectedUsersAsync(List<DisputeCreatedInfo> disputesCreated)
        {
            try
            {
                foreach (var dispute in disputesCreated)
                {
                    await SendDisputeNotificationAsync(dispute);
                }
            }
            catch (Exception ex)
            {
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

                // 🔔 NOTIF-CENTRAL: trigger realtime post-guardado (best-effort).
                await _inAppNotifications.BroadcastCreatedAsync(new[] { notification });
            }
            catch (Exception ex)
            {
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
                    return;
                }

                // Determinar quién es el usuario afectado (el que NO eliminó la cuenta)
                var affectedUser = searchHire.Client?.Email == dispute.AffectedPartyEmail 
                    ? searchHire.Client 
                    : searchHire.Expert;

                if (affectedUser == null)
                {
                    return;
                }

                var serviceName = searchHire.SearchService?.ServiceType?.Name ?? "Servicio";
                var isClientAffected = searchHire.ClientId == affectedUser.Id;

                        var notification = new Notification
                        {
                            UserId = affectedUser.Id,
                            Title = "Contratación Procesada - Cuenta Eliminada",
                            Message = isClientAffected
                                ? $"El experto del servicio '{serviceName}' ha eliminado su cuenta. Se ha procesado automáticamente un reembolso a tu favor por el importe completo del servicio."
                                : $"El cliente del servicio '{serviceName}' ha eliminado su cuenta. Se ha procesado automáticamente el pago del servicio a tu favor.",
                            Type = "transaction_processed",
                            Read = false,
                            CreatedAt = DateTime.UtcNow
                        };

                _context.Notifications.Add(notification);
                await _context.SaveChangesAsync();

                // 🔔 NOTIF-CENTRAL: trigger realtime post-guardado (best-effort).
                await _inAppNotifications.BroadcastCreatedAsync(new[] { notification });
            }
            catch (Exception ex)
            {
                throw;
            }
        }
    }
}
