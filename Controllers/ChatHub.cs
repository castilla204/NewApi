using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using newApi.Services;
using newApi.DataLayer.Models;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace newApi.Controllers
{
    /// <summary>
    /// ✅ MEJORAS 2025: ChatHub optimizado con mejores prácticas SignalR
    /// - Reconexión automática mejorada
    /// - Manejo robusto de errores
    /// - Estado de usuario (online/offline, typing)
    /// - Gestión eficiente de grupos y conexiones
    /// - Logging detallado para debugging
    /// </summary>
    public class ChatHub : Hub
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        
        // ✅ MEJORA 2025: Diccionario concurrente mejorado para tracking de conexiones
        // Key: ConnectionId, Value: UserId
        private static readonly ConcurrentDictionary<string, int> _connectionUsers = new();
        
        // ✅ MEJORA 2025: Tracking de usuarios por conversación para estado online/offline
        // Key: ConversationId, Value: HashSet de UserIds
        private static readonly ConcurrentDictionary<int, HashSet<int>> _conversationUsers = new();
        
        // ✅ MEJORA 2025: Tracking de usuarios escribiendo por conversación
        // Key: ConversationId, Value: HashSet de UserIds
        private static readonly ConcurrentDictionary<int, HashSet<int>> _typingUsers = new();

        /// <summary>
        /// Constructor del ChatHub
        /// </summary>
        /// <param name="serviceScopeFactory">Factory para crear scopes de servicios</param>
        public ChatHub(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        /// <summary>
        /// ✅ MEJORA 2025: OnConnectedAsync mejorado con mejor manejo de errores y logging
        /// </summary>
        public override async Task OnConnectedAsync()
        {
            try
            {
                if (!(Context.User?.Identity?.IsAuthenticated ?? false))
                {
                    await LogWarningAsync("SignalR connection failed: unauthenticated user",
                        new { ConnectionId = Context.ConnectionId, UserAgent = Context.GetHttpContext()?.Request.Headers["User-Agent"].ToString() });
                    Context.Abort();
                    return;
                }

                if (!int.TryParse(Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    await LogWarningAsync("SignalR connection failed: missing user identifier",
                        new { ConnectionId = Context.ConnectionId });
                    Context.Abort();
                    return;
                }

                // ✅ MEJORA 2025: Agregar conexión al tracking
                _connectionUsers[Context.ConnectionId] = userId;
                
                // ✅ MEJORA 2025: Agregar usuario a grupo global para notificaciones
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");
                
                await LogInfoAsync("User connected to SignalR", userId, null,
                    new { 
                        ConnectionId = Context.ConnectionId,
                        UserId = userId,
                        UserAgent = Context.GetHttpContext()?.Request.Headers["User-Agent"].ToString()
                    });

                await base.OnConnectedAsync();
            }
            catch (Exception ex)
            {
                await LogErrorAsync("Error in OnConnectedAsync", ex,
                    new { ConnectionId = Context.ConnectionId });
                Context.Abort();
            }
        }

        /// <summary>
        /// ✅ MEJORA 2025: OnDisconnectedAsync mejorado con limpieza de grupos y estado
        /// </summary>
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            try
            {
                if (_connectionUsers.TryRemove(Context.ConnectionId, out var userId))
                {
                    // ✅ MEJORA 2025: Limpiar usuario de todas las conversaciones
                    await CleanupUserFromConversations(userId);
                    
                    // ✅ MEJORA 2025: Remover de grupo de usuario
                    await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user-{userId}");
                    
                    await LogInfoAsync("User disconnected from SignalR", userId, null,
                        new { 
                            ConnectionId = Context.ConnectionId, 
                            Error = exception?.Message,
                            StackTrace = exception?.StackTrace
                        });
                }

                await base.OnDisconnectedAsync(exception);
            }
            catch (Exception ex)
            {
                await LogErrorAsync("Error in OnDisconnectedAsync", ex,
                    new { ConnectionId = Context.ConnectionId });
            }
        }

        /// <summary>
        /// ✅ MEJORA 2025: JoinConversation mejorado con notificación de usuario online
        /// </summary>
        public async Task JoinConversation(int conversationId)
        {
            try
            {
                var userId = GetUserIdOrThrow();
                using var scope = _serviceScopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var authService = scope.ServiceProvider.GetRequiredService<IAuthorizationServices>();

                var conversation = await db.Conversations
                    .Include(c => c.SearchHire)
                    .FirstOrDefaultAsync(c => c.Id == conversationId);

                if (conversation == null)
                {
                    throw new HubException("Conversation not found");
                }

                var isClient = conversation.ClientId.HasValue && conversation.ClientId.Value == userId;
                var isExpert = conversation.ExpertId.HasValue && conversation.ExpertId.Value == userId;
                var isAdmin = Context.User != null && authService.IsAdmin(Context.User);

                if (!isClient && !isExpert && !isAdmin)
                {
                    await LogWarningAsync("Unauthorized attempt to join conversation",
                        new { ConnectionId = Context.ConnectionId, ConversationId = conversationId, UserId = userId });
                    throw new HubException("You are not authorized to join this conversation");
                }

                var groupName = $"conversation-{conversationId}";
                await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

                // ✅ MEJORA 2025: Agregar usuario a tracking de conversación
                _conversationUsers.AddOrUpdate(conversationId, 
                    new HashSet<int> { userId },
                    (key, existing) => { existing.Add(userId); return existing; });

                await LogInfoAsync("User joined conversation via SignalR", userId, conversationId,
                    new { ConnectionId = Context.ConnectionId, ConversationId = conversationId });

                // ✅ MEJORA 2025: Notificar a otros usuarios que este usuario está online
                await Clients.GroupExcept(groupName, Context.ConnectionId)
                    .SendAsync("UserJoinedConversation", new { UserId = userId, ConversationId = conversationId });
            }
            catch (HubException)
            {
                throw; // Re-throw HubException sin logging adicional
            }
            catch (Exception ex)
            {
                await LogErrorAsync("Error joining conversation", ex,
                    new { ConversationId = conversationId, ConnectionId = Context.ConnectionId });
                throw new HubException("An error occurred while joining the conversation");
            }
        }

        /// <summary>
        /// ✅ MEJORA 2025: LeaveConversation mejorado con notificación de usuario offline
        /// </summary>
        public async Task LeaveConversation(int conversationId)
        {
            try
            {
                var userId = GetUserIdOrThrow();
                var groupName = $"conversation-{conversationId}";
                
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);

                // ✅ MEJORA 2025: Remover usuario de tracking de conversación
                if (_conversationUsers.TryGetValue(conversationId, out var users))
                {
                    users.Remove(userId);
                    if (users.Count == 0)
                    {
                        _conversationUsers.TryRemove(conversationId, out _);
                    }
                }

                // ✅ MEJORA 2025: Remover de usuarios escribiendo
                if (_typingUsers.TryGetValue(conversationId, out var typingUsers))
                {
                    typingUsers.Remove(userId);
                    if (typingUsers.Count == 0)
                    {
                        _typingUsers.TryRemove(conversationId, out _);
                    }
                }

                await LogInfoAsync("User left conversation via SignalR", userId, conversationId,
                    new { ConnectionId = Context.ConnectionId, ConversationId = conversationId });

                // ✅ MEJORA 2025: Notificar a otros usuarios que este usuario salió
                await Clients.GroupExcept(groupName, Context.ConnectionId)
                    .SendAsync("UserLeftConversation", new { UserId = userId, ConversationId = conversationId });
            }
            catch (Exception ex)
            {
                await LogErrorAsync("Error leaving conversation", ex,
                    new { ConversationId = conversationId, ConnectionId = Context.ConnectionId });
            }
        }

        /// <summary>
        /// ✅ NUEVO 2025: Método para indicar que el usuario está escribiendo
        /// </summary>
        public async Task UserTyping(int conversationId, bool isTyping)
        {
            try
            {
                var userId = GetUserIdOrThrow();
                var groupName = $"conversation-{conversationId}";

                if (isTyping)
                {
                    // Agregar a usuarios escribiendo
                    _typingUsers.AddOrUpdate(conversationId,
                        new HashSet<int> { userId },
                        (key, existing) => { existing.Add(userId); return existing; });
                }
                else
                {
                    // Remover de usuarios escribiendo
                    if (_typingUsers.TryGetValue(conversationId, out var typingUsers))
                    {
                        typingUsers.Remove(userId);
                        if (typingUsers.Count == 0)
                        {
                            _typingUsers.TryRemove(conversationId, out _);
                        }
                    }
                }

                // Notificar a otros usuarios en la conversación
                await Clients.GroupExcept(groupName, Context.ConnectionId)
                    .SendAsync("UserTyping", new { UserId = userId, ConversationId = conversationId, IsTyping = isTyping });
            }
            catch (Exception ex)
            {
                await LogErrorAsync("Error in UserTyping", ex,
                    new { ConversationId = conversationId, ConnectionId = Context.ConnectionId });
            }
        }

        /// <summary>
        /// ✅ NUEVO 2025: Obtener usuarios online en una conversación
        /// </summary>
        public async Task GetOnlineUsers(int conversationId)
        {
            try
            {
                var userId = GetUserIdOrThrow();
                
                // Verificar autorización
                using var scope = _serviceScopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var authService = scope.ServiceProvider.GetRequiredService<IAuthorizationServices>();

                var conversation = await db.Conversations
                    .FirstOrDefaultAsync(c => c.Id == conversationId);

                if (conversation == null)
                {
                    throw new HubException("Conversation not found");
                }

                var isClient = conversation.ClientId.HasValue && conversation.ClientId.Value == userId;
                var isExpert = conversation.ExpertId.HasValue && conversation.ExpertId.Value == userId;
                var isAdmin = Context.User != null && authService.IsAdmin(Context.User);

                if (!isClient && !isExpert && !isAdmin)
                {
                    throw new HubException("You are not authorized to view this conversation");
                }

                // Obtener usuarios online en esta conversación
                var onlineUsers = _conversationUsers.TryGetValue(conversationId, out var users) 
                    ? users.ToList() 
                    : new List<int>();

                await Clients.Caller.SendAsync("OnlineUsers", new { ConversationId = conversationId, UserIds = onlineUsers });
            }
            catch (HubException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await LogErrorAsync("Error getting online users", ex,
                    new { ConversationId = conversationId, ConnectionId = Context.ConnectionId });
                throw new HubException("An error occurred while getting online users");
            }
        }

        /// <summary>
        /// ✅ MEJORA 2025: Limpiar usuario de todas las conversaciones al desconectarse
        /// </summary>
        private Task CleanupUserFromConversations(int userId)
        {
            var conversationsToClean = new List<int>();

            foreach (var kvp in _conversationUsers)
            {
                if (kvp.Value.Contains(userId))
                {
                    kvp.Value.Remove(userId);
                    if (kvp.Value.Count == 0)
                    {
                        conversationsToClean.Add(kvp.Key);
                    }
                }
            }

            foreach (var conversationId in conversationsToClean)
            {
                _conversationUsers.TryRemove(conversationId, out _);
            }

            // Limpiar también de usuarios escribiendo
            foreach (var kvp in _typingUsers)
            {
                if (kvp.Value.Contains(userId))
                {
                    kvp.Value.Remove(userId);
                    if (kvp.Value.Count == 0)
                    {
                        _typingUsers.TryRemove(kvp.Key, out _);
                    }
                }
            }
            
            return Task.CompletedTask;
        }

        private int GetUserIdOrThrow()
        {
            if (!int.TryParse(Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
            {
                throw new HubException("Invalid user context");
            }
            return userId;
        }

        private async Task LogInfoAsync(string message, int? userId, int? conversationId, object additionalData)
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                await loggingService.LogInfoAsync(
                    message: message,
                    details: message,
                    userId: userId,
                    source: "ChatHub",
                    relatedEntityType: conversationId.HasValue ? "Conversation" : "SignalR",
                    relatedEntityId: conversationId,
                    additionalData: additionalData,
                    notifyUser: false // No notificar a usuarios sobre logs de conexión
                );
            }
            catch
            {
                // Silently fail logging - no debe interrumpir el flujo de SignalR
            }
        }

        private async Task LogWarningAsync(string message, object additionalData)
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                await loggingService.LogWarningAsync(
                    message: message,
                    details: message,
                    source: "ChatHub",
                    relatedEntityType: "SignalR",
                    additionalData: additionalData
                );
            }
            catch
            {
                // Silently fail logging
            }
        }

        private async Task LogErrorAsync(string message, Exception exception, object additionalData)
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                await loggingService.LogErrorAsync(
                    message: message,
                    details: $"{message}: {exception.Message}",
                    source: "ChatHub",
                    relatedEntityType: "SignalR",
                    additionalData: new
                    {
                        Exception = exception.Message,
                        StackTrace = exception.StackTrace,
                        AdditionalData = additionalData
                    }
                );
            }
            catch
            {
                // Silently fail logging
            }
        }
    }
}