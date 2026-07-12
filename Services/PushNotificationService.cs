using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using newApi.DataLayer.Models;

namespace newApi.Services
{
    public interface IPushNotificationService
    {
        bool IsEnabled { get; }
        Task SendToUserAsync(int userId, string title, string body, string? url = null, string? type = null);
    }

    /// <summary>
    /// 📲 PUSH-CENTRAL: envía push FCM a todos los dispositivos de un usuario. Gated por
    /// credencial (no-op + warning si falta Firebase:ServiceAccountJson, igual patrón que
    /// SmsService.IsEnabled). Best-effort: nunca lanza. Limpia tokens muertos.
    /// </summary>
    public class PushNotificationService : IPushNotificationService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PushNotificationService> _logger;
        private readonly FirebaseApp? _app;

        public bool IsEnabled => _app != null;

        public PushNotificationService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<PushNotificationService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;

            var json = config["Firebase:ServiceAccountJson"];
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("[PUSH] Firebase no configurado (falta Firebase:ServiceAccountJson). Push deshabilitado (no-op). Revisar env var firebase-service-account-json.");
                _app = null;
                return;
            }
            try
            {
                _app = FirebaseApp.GetInstance("inspecciono-push")
                    ?? FirebaseApp.Create(new AppOptions { Credential = GoogleCredential.FromJson(json) }, "inspecciono-push");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PUSH] No se pudo inicializar FirebaseApp. Push deshabilitado.");
                _app = null;
            }
        }

        public async Task SendToUserAsync(int userId, string title, string body, string? url = null, string? type = null)
        {
            if (!IsEnabled)
            {
                _logger.LogWarning("[PUSH] a user {UserId} OMITIDO: Firebase no habilitado.", userId);
                return;
            }
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var tokens = await db.DeviceTokens
                    .Where(d => d.UserId == userId)
                    .Select(d => d.Token)
                    .ToListAsync();
                if (tokens.Count == 0) return;

                var data = new Dictionary<string, string>();
                if (!string.IsNullOrWhiteSpace(url)) data["url"] = url!;
                if (!string.IsNullOrWhiteSpace(type)) data["type"] = type!;

                var message = new MulticastMessage
                {
                    Tokens = tokens,
                    Notification = new FirebaseAdmin.Messaging.Notification { Title = title, Body = body },
                    Data = data,
                };

                var messaging = FirebaseMessaging.GetMessaging(_app);
                var response = await messaging.SendEachForMulticastAsync(message);

                if (response.FailureCount > 0)
                {
                    var toDelete = new List<string>();
                    for (int i = 0; i < response.Responses.Count; i++)
                    {
                        var r = response.Responses[i];
                        if (!r.IsSuccess && DeadTokenClassifier.ShouldDelete(r.Exception?.MessagingErrorCode))
                            toDelete.Add(tokens[i]);
                    }
                    if (toDelete.Count > 0)
                    {
                        await db.DeviceTokens.Where(d => toDelete.Contains(d.Token)).ExecuteDeleteAsync();
                        _logger.LogInformation("[PUSH] {Count} token(s) muerto(s) eliminados para user {UserId}.", toDelete.Count, userId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PUSH] Envío a user {UserId} falló (no crítico).", userId);
            }
        }
    }

    public static class DeadTokenClassifier
    {
        public static bool ShouldDelete(MessagingErrorCode? code) =>
            code == MessagingErrorCode.Unregistered || code == MessagingErrorCode.InvalidArgument;
    }
}
