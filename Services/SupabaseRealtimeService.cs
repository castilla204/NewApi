using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace newApi.Services
{
    /// <summary>
    /// Servicio para interactuar con Supabase Realtime
    /// Reemplaza SignalR para mensajería en tiempo real
    /// </summary>
    public interface ISupabaseRealtimeService
    {
        /// <summary>
        /// Envía un broadcast a un canal específico (ej: typing indicator)
        /// </summary>
        Task BroadcastToChannelAsync(string channel, string eventName, object payload);
        
        /// <summary>
        /// Envía notificación de nuevo mensaje a una conversación
        /// </summary>
        Task NotifyNewMessageAsync(int conversationId, object messageData);

        Task NotifyMessageUpdatedAsync(int conversationId, object messageData);
        
        /// <summary>
        /// Envía notificación de usuario escribiendo
        /// </summary>
        Task NotifyUserTypingAsync(int conversationId, int userId, bool isTyping);
        
        /// <summary>
        /// Envía notificación de usuario online/offline en conversación
        /// </summary>
        Task NotifyUserPresenceAsync(int conversationId, int userId, bool isOnline);

        /// <summary>
        /// 🔔 NOTIF-RT: difunde una notificación in-app recién creada para que el
        /// frontend actualice campana/inbox al instante. userId null = notificación
        /// global de administradores (canal notifications:admins).
        /// </summary>
        Task NotifyUserNotificationAsync(int? userId, object notificationData);

        /// <summary>
        /// 🔔 NOTIF-RT: difunde VARIOS eventos en UNA sola llamada HTTP a la API de
        /// broadcast de Supabase (acepta un array de messages). Para lotes de
        /// notificaciones (p.ej. avisos de suscripción a N usuarios).
        /// </summary>
        Task BroadcastBatchAsync(IReadOnlyCollection<(string Channel, string EventName, object Payload)> items);
    }

    public class SupabaseRealtimeService : ISupabaseRealtimeService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<SupabaseRealtimeService> _logger;
        private readonly string _supabaseUrl;
        private readonly string _supabaseServiceKey;

        public SupabaseRealtimeService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<SupabaseRealtimeService> logger)
        {
            _httpClient = httpClientFactory.CreateClient("Supabase");
            _logger = logger;
            
            // Obtener configuración de Supabase
            _supabaseUrl = configuration["Supabase:Url"] 
                ?? Environment.GetEnvironmentVariable("SUPABASE_URL")
                ?? "https://cckrnifvbrwuagzlsrbj.supabase.co";
            
            _supabaseServiceKey = configuration["Supabase:ServiceRoleKey"] 
                ?? configuration["Supabase:ServiceKey"] // Mantener compatibilidad
                ?? Environment.GetEnvironmentVariable("SUPABASE_SERVICE_KEY")
                ?? throw new InvalidOperationException("Supabase Service Role Key not configured. Configure Supabase:ServiceRoleKey in appsettings");
            
            _httpClient.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Bearer", _supabaseServiceKey);
            _httpClient.DefaultRequestHeaders.Add("apikey", _supabaseServiceKey);
        }

        /// <summary>
        /// Envía un broadcast usando la API REST de Supabase Realtime
        /// </summary>
        public Task BroadcastToChannelAsync(string channel, string eventName, object payload)
            => BroadcastBatchAsync(new[] { (channel, eventName, payload) });

        /// <inheritdoc />
        public async Task BroadcastBatchAsync(IReadOnlyCollection<(string Channel, string EventName, object Payload)> items)
        {
            if (items == null || items.Count == 0) return;
            var channel = items.First().Channel;
            var eventName = items.First().EventName;
            try
            {
                var broadcastUrl = $"{_supabaseUrl}/realtime/v1/api/broadcast";

                var message = new
                {
                    messages = items.Select(i => new
                    {
                        topic = i.Channel,
                        @event = i.EventName,
                        payload = i.Payload
                    }).ToArray()
                };

                var serialized = JsonSerializer.Serialize(message);

                // ✅ LOG: Antes de enviar el broadcast
                _logger.LogInformation("🔔 [SupabaseRealtime] Enviando broadcast ({Count} mensaje/s) — primero: canal {Channel}, evento {Event}", items.Count, channel, eventName);
                _logger.LogInformation("🔔 [SupabaseRealtime] URL: {Url}", broadcastUrl);

                // ✅ RETRY: un fallo transitorio (red/5xx de Supabase) dejaba el mensaje
                // sin entregar en directo y el receptor no lo veía hasta el polling de
                // respaldo. Reintentamos una vez con backoff corto antes de rendirnos.
                const int maxAttempts = 2;
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    using var content = new StringContent(serialized, Encoding.UTF8, "application/json");
                    HttpResponseMessage? response = null;
                    try
                    {
                        response = await _httpClient.PostAsync(broadcastUrl, content);
                    }
                    catch (Exception sendEx) when (attempt < maxAttempts)
                    {
                        _logger.LogWarning(sendEx, "⚠️ [SupabaseRealtime] Intento {Attempt}/{Max} falló para canal {Channel}; reintentando…", attempt, maxAttempts, channel);
                        await Task.Delay(400);
                        continue;
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        var responseContent = await response.Content.ReadAsStringAsync();
                        _logger.LogInformation("✅ [SupabaseRealtime] Broadcast enviado exitosamente a canal: {Channel}, evento: {Event}", channel, eventName);
                        _logger.LogInformation("✅ [SupabaseRealtime] Respuesta: {Response}", responseContent);
                        return;
                    }

                    var errorContent = await response.Content.ReadAsStringAsync();
                    if (attempt < maxAttempts && (int)response.StatusCode >= 500)
                    {
                        _logger.LogWarning("⚠️ [SupabaseRealtime] {StatusCode} en intento {Attempt}/{Max} para canal {Channel}; reintentando…", response.StatusCode, attempt, maxAttempts, channel);
                        await Task.Delay(400);
                        continue;
                    }

                    _logger.LogError("❌ [SupabaseRealtime] Error broadcasting: {StatusCode} - {Error}",
                        response.StatusCode, errorContent);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception broadcasting to channel {Channel}", channel);
            }
        }

        /// <summary>
        /// Notifica un nuevo mensaje en una conversación
        /// </summary>
        public async Task NotifyNewMessageAsync(int conversationId, object messageData)
        {
            var channel = $"conversation:{conversationId}";
            _logger.LogInformation("📨 [SupabaseRealtime] Notificando nuevo mensaje - ConversationId: {ConversationId}, Canal: {Channel}", conversationId, channel);
            
            // ✅ Serializar el payload para logging
            try
            {
                var payloadJson = JsonSerializer.Serialize(messageData);
                _logger.LogInformation("📨 [SupabaseRealtime] Payload del mensaje: {Payload}", payloadJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ [SupabaseRealtime] No se pudo serializar el payload para logging: {Error}", ex.Message);
            }
            
            await BroadcastToChannelAsync(channel, "new_message", messageData);
        }

        public async Task NotifyMessageUpdatedAsync(int conversationId, object messageData)
        {
            var channel = $"conversation:{conversationId}";
            await BroadcastToChannelAsync(channel, "message_updated", messageData);
        }

        /// <summary>
        /// Notifica que un usuario está escribiendo
        /// </summary>
        public async Task NotifyUserTypingAsync(int conversationId, int userId, bool isTyping)
        {
            var channel = $"conversation:{conversationId}";
            var payload = new
            {
                userId,
                conversationId,
                isTyping,
                timestamp = DateTime.UtcNow
            };
            
            await BroadcastToChannelAsync(channel, "typing", payload);
        }

        /// <summary>
        /// Notifica cambio de presencia de usuario
        /// </summary>
        public async Task NotifyUserPresenceAsync(int conversationId, int userId, bool isOnline)
        {
            var channel = $"conversation:{conversationId}";
            var payload = new
            {
                userId,
                conversationId,
                isOnline,
                timestamp = DateTime.UtcNow
            };
            
            await BroadcastToChannelAsync(channel, isOnline ? "user_joined" : "user_left", payload);
        }

        /// <inheritdoc />
        public async Task NotifyUserNotificationAsync(int? userId, object notificationData)
        {
            // Canal por usuario (la campana del frontend se suscribe a su propio canal);
            // las notificaciones globales de admins van a un canal compartido que solo
            // los admins escuchan (el ENDPOINT sigue siendo la fuente de verdad con
            // autorización — el broadcast solo dispara el refetch).
            var channel = userId.HasValue ? $"notifications:user:{userId.Value}" : "notifications:admins";
            await BroadcastToChannelAsync(channel, "new_notification", notificationData);
        }
    }
}
