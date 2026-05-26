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
        public async Task BroadcastToChannelAsync(string channel, string eventName, object payload)
        {
            try
            {
                var broadcastUrl = $"{_supabaseUrl}/realtime/v1/api/broadcast";
                
                var message = new
                {
                    messages = new[]
                    {
                        new
                        {
                            topic = channel,
                            @event = eventName,
                            payload = payload
                        }
                    }
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(message),
                    Encoding.UTF8,
                    "application/json"
                );

                // ✅ LOG: Antes de enviar el broadcast
                _logger.LogInformation("🔔 [SupabaseRealtime] Enviando broadcast a canal: {Channel}, evento: {Event}", channel, eventName);
                _logger.LogInformation("🔔 [SupabaseRealtime] URL: {Url}", broadcastUrl);
                
                var response = await _httpClient.PostAsync(broadcastUrl, content);
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("❌ [SupabaseRealtime] Error broadcasting: {StatusCode} - {Error}", 
                        response.StatusCode, errorContent);
                }
                else
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    _logger.LogInformation("✅ [SupabaseRealtime] Broadcast enviado exitosamente a canal: {Channel}, evento: {Event}", channel, eventName);
                    _logger.LogInformation("✅ [SupabaseRealtime] Respuesta: {Response}", responseContent);
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
    }
}
