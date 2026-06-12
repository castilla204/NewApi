using Microsoft.EntityFrameworkCore;
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

        /// <summary>
        /// 📱 Crea la notificación in-app Y, si es una acción importante (sendSms),
        /// envía además un SMS al usuario si tiene teléfono verificado y SMS habilitado.
        /// El SMS es best-effort (un fallo no rompe la notificación in-app).
        /// </summary>
        Task<Notification> CreateAsync(int? userId, string title, string message, string type, bool sendSms, string? smsText = null, string? url = null, string? imageUrl = null);

        /// <summary>Difunde el trigger realtime de filas YA guardadas/commiteadas (lote en una sola llamada HTTP).</summary>
        Task BroadcastCreatedAsync(IEnumerable<Notification> notifications);

        /// <summary>
        /// 📱 Envía un SMS a un usuario SOLO si tiene teléfono verificado (PhoneVerified)
        /// y SMS habilitado. Best-effort. Para sitios que insertan la notificación en su
        /// propia transacción y solo quieren el refuerzo por SMS tras el commit.
        /// </summary>
        Task SendImportantSmsAsync(int userId, string smsText);
    }

    public class InAppNotificationService : IInAppNotificationService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ISupabaseRealtimeService _realtime;
        private readonly ISmsService _sms;

        public InAppNotificationService(IServiceScopeFactory scopeFactory, ISupabaseRealtimeService realtime, ISmsService sms)
        {
            _scopeFactory = scopeFactory;
            _realtime = realtime;
            _sms = sms;
        }

        /// <inheritdoc />
        public Task<Notification> CreateAsync(int? userId, string title, string message, string type, string? url = null, string? imageUrl = null)
            => CreateAsync(userId, title, message, type, sendSms: false, smsText: null, url: url, imageUrl: imageUrl);

        /// <inheritdoc />
        public async Task<Notification> CreateAsync(int? userId, string title, string message, string type, bool sendSms, string? smsText = null, string? url = null, string? imageUrl = null)
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

            // 📱 SMS solo para acciones importantes y a un destinatario concreto (no globales de admin).
            if (sendSms && userId.HasValue)
            {
                await SendImportantSmsAsync(userId.Value, smsText ?? message);
            }
            return notification;
        }

        /// <inheritdoc />
        public async Task SendImportantSmsAsync(int userId, string smsText)
        {
            try
            {
                if (!_sms.IsEnabled || string.IsNullOrWhiteSpace(smsText)) return;

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var user = await db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => new { u.PhoneNumber, u.PhoneVerified, u.PhoneLineType })
                    .FirstOrDefaultAsync();

                // Solo a teléfonos VERIFICADOS (Stripe KYC para expertos, checkout para clientes).
                if (user == null || !user.PhoneVerified || string.IsNullOrWhiteSpace(user.PhoneNumber)) return;

                // 📱 Un FIJO no recibe SMS: no quemar saldo de Twilio. El panel del experto
                // ya le pide cargar un móvil y verificarlo por OTP.
                if (string.Equals(user.PhoneLineType, "landline", StringComparison.OrdinalIgnoreCase)) return;

                await _sms.SendSmsAsync(user.PhoneNumber, smsText);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NOTIF-CENTRAL] SMS importante falló (no crítico): {ex.Message}");
            }
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
