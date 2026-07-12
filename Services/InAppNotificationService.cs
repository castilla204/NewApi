using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        private readonly IPushNotificationService _push;
        private readonly ILogger<InAppNotificationService> _logger;

        public InAppNotificationService(IServiceScopeFactory scopeFactory, ISupabaseRealtimeService realtime, ISmsService sms, IPushNotificationService push, ILogger<InAppNotificationService> logger)
        {
            _scopeFactory = scopeFactory;
            _realtime = realtime;
            _sms = sms;
            _push = push;
            _logger = logger;
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
                // 📲 Mismo tier "importante" que el SMS → push FCM al móvil.
                await _push.SendToUserAsync(userId.Value, title, message, url, type);
            }
            return notification;
        }

        /// <inheritdoc />
        public async Task SendImportantSmsAsync(int userId, string smsText)
        {
            try
            {
                // SMS-LOG FIX: antes cada uno de estos descartes era un `return` MUDO → un experto que
                // recibía el email pero no el SMS era indiagnosticable sin entrar a la BD a mano (hicieron
                // falta 5 agentes para diagnosticar uno). Ahora cada descarte deja rastro con su motivo.
                if (string.IsNullOrWhiteSpace(smsText)) return; // nada que enviar (no es un fallo)
                if (!_sms.IsEnabled)
                {
                    _logger.LogWarning("[NOTIF-CENTRAL] SMS a user {UserId} OMITIDO: Twilio no habilitado (faltan credenciales o emisor en config). Revisar env vars twilio-* en Render.", userId);
                    return;
                }

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var user = await db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => new { u.PhoneNumber, u.PhoneVerified, u.PhoneLineType })
                    .FirstOrDefaultAsync();

                // Solo a teléfonos VERIFICADOS (Stripe KYC para expertos, checkout para clientes).
                if (user == null || !user.PhoneVerified || string.IsNullOrWhiteSpace(user.PhoneNumber))
                {
                    // Nivel Information: es ESPERADO para clientes (no verifican móvil). Si el destinatario
                    // es un EXPERTO contratado, este log revela que su móvil no está verificado (causa del bug).
                    _logger.LogInformation(
                        "[NOTIF-CENTRAL] SMS a user {UserId} OMITIDO: {Reason} (HasUser={HasUser}, PhoneVerified={Verified}, HasPhone={HasPhone}).",
                        userId,
                        user == null ? "usuario no encontrado" : (!user.PhoneVerified ? "móvil no verificado" : "sin número de teléfono"),
                        user != null, user?.PhoneVerified ?? false, !string.IsNullOrWhiteSpace(user?.PhoneNumber));
                    return;
                }

                // 📱 Un FIJO no recibe SMS: no quemar saldo de Twilio. El panel del experto
                // ya le pide cargar un móvil y verificarlo por OTP.
                if (string.Equals(user.PhoneLineType, "landline", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[NOTIF-CENTRAL] SMS a user {UserId} OMITIDO: el número está clasificado como fijo (landline).", userId);
                    return;
                }

                await _sms.SendSmsAsync(user.PhoneNumber, smsText);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[NOTIF-CENTRAL] SMS importante a user {UserId} falló (no crítico).", userId);
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
                // 🛡️ FIX (auditoría 2026-07-06): logging persistente en vez de Console.WriteLine
                // (que se pierde en prod), en paridad con el resto de la clase que ya usa _logger.
                _logger.LogWarning(ex, "[NOTIF-CENTRAL] Broadcast de notificación falló (no crítico).");
            }
        }
    }
}
