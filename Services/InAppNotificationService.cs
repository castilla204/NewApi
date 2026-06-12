using Microsoft.Extensions.DependencyInjection;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    /// <summary>
    /// 🔔 NOTIF-CENTRAL (2026-06-12): punto ÚNICO para notificaciones in-app.
    ///
    /// Dos modos según el sitio llamador:
    ///  - <see cref="CreateAsync"/>: para sitios SIN acoplamiento transaccional
    ///    (creación manual del panel admin, avisos standalone). Crea la fila en su
    ///    propio scope, guarda y difunde el trigger realtime.
    ///  - <see cref="BroadcastCreatedAsync"/>: para sitios que insertan la fila
    ///    DENTRO de su propia transacción (dinero, estados). Insertan y commitean
    ///    como siempre, y DESPUÉS del commit llaman aquí solo para difundir.
    ///
    /// El broadcast es un trigger MÍNIMO (id + timestamp) por canal
    /// notifications:user:{id} (o notifications:admins si UserId es null): el
    /// contenido siempre se lee del endpoint autenticado, así que un canal
    /// adivinable no filtra nada. Best-effort: un fallo aquí nunca rompe el flujo
    /// (el frontend tiene polling de respaldo). Los lotes salen en UNA llamada HTTP.
    /// </summary>
    public interface IInAppNotificationService
    {
        /// <summary>Crea + guarda + difunde (scope propio; NO usar dentro de una transacción del llamador).</summary>
        Task<Notification> CreateAsync(int? userId, string title, string message, string type, string? url = null, string? imageUrl = null);

        /// <summary>Difunde el trigger realtime de filas YA guardadas/commiteadas (lote en una sola llamada HTTP).</summary>
        Task BroadcastCreatedAsync(IEnumerable<Notification> notifications);
    }

    public class InAppNotificationService : IInAppNotificationService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ISupabaseRealtimeService _realtime;

        public InAppNotificationService(IServiceScopeFactory scopeFactory, ISupabaseRealtimeService realtime)
        {
            _scopeFactory = scopeFactory;
            _realtime = realtime;
        }

        /// <inheritdoc />
        public async Task<Notification> CreateAsync(int? userId, string title, string message, string type, string? url = null, string? imageUrl = null)
        {
            var notification = new Notification
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Title = title,
                Message = message,
                Type = type,
                Url = url,
                ImageUrl = imageUrl,
                Read = false,
                CreatedAt = DateTime.UtcNow,
            };

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Notifications.Add(notification);
            await db.SaveChangesAsync();

            await BroadcastCreatedAsync(new[] { notification });
            return notification;
        }

        /// <inheritdoc />
        public async Task BroadcastCreatedAsync(IEnumerable<Notification> notifications)
        {
            try
            {
                var items = notifications
                    .Where(n => n != null)
                    .Select(n => (
                        Channel: n.UserId.HasValue ? $"notifications:user:{n.UserId.Value}" : "notifications:admins",
                        EventName: "new_notification",
                        Payload: (object)new { id = n.Id, createdAt = n.CreatedAt }))
                    .ToList();

                if (items.Count == 0) return;
                await _realtime.BroadcastBatchAsync(items);
            }
            catch (Exception ex)
            {
                // Best-effort: el polling del frontend es la red de seguridad.
                Console.WriteLine($"[NOTIF-CENTRAL] Broadcast de notificación falló (no crítico): {ex.Message}");
            }
        }
    }
}
