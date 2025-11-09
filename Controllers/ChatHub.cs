using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using newApi.Services;

public class ChatHub : Hub
{
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public ChatHub(IConfiguration configuration, IServiceScopeFactory serviceScopeFactory)
    {
        _configuration = configuration;
        _serviceScopeFactory = serviceScopeFactory;
    }

    public override async Task OnConnectedAsync()
    {
        var token = Context.GetHttpContext()?.Request.Query["access_token"].ToString();
        if (string.IsNullOrEmpty(token))
        {
            // ✅ LOG EN BD: Error de autenticación en SignalR
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                await loggingService.LogWarningAsync(
                    message: "SignalR connection failed: Missing authentication token",
                    details: $"SignalR connection attempt without authentication token. ConnectionId: {Context.ConnectionId}",
                    source: "ChatHub.OnConnectedAsync",
                    relatedEntityType: "SignalR",
                    additionalData: new { ConnectionId = Context.ConnectionId }
                );
            }
            
            throw new HubException("Missing authentication token");
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(_configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key not found"));
            handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _configuration["Jwt:Issuer"] ?? "YourIssuer",
                ValidAudience = _configuration["Jwt:Audience"] ?? "YourAudience",
                IssuerSigningKey = new SymmetricSecurityKey(key)
            }, out var validatedToken);

            var jwtToken = (JwtSecurityToken)validatedToken;
            var userId = jwtToken.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
            var conversationId = Context.GetHttpContext()?.Request.Query["conversationId"].ToString();

            if (userId != null && conversationId != null)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"conversation-{conversationId}");
                // ✅ LOG EN BD: Usuario conectado a conversación
                if (int.TryParse(userId, out int userIdInt) && int.TryParse(conversationId, out int conversationIdInt))
                {
                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                        await loggingService.LogInfoAsync(
                            message: "User joined conversation via SignalR",
                            details: $"User {userId} joined conversation {conversationId} on SignalR connect. ConnectionId: {Context.ConnectionId}",
                            userId: userIdInt,
                            source: "ChatHub.OnConnectedAsync",
                            relatedEntityType: "Conversation",
                            relatedEntityId: conversationIdInt,
                            additionalData: new { ConnectionId = Context.ConnectionId, ConversationId = conversationIdInt }
                        );
                    }
                }
            }
            else
            {
                
                // ✅ LOG EN BD: Error de conexión SignalR
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                    await loggingService.LogWarningAsync(
                        message: "SignalR connection failed: Invalid user ID or conversation ID",
                        details: $"SignalR connection failed. UserId: {userId}, ConversationId: {conversationId}, ConnectionId: {Context.ConnectionId}",
                        source: "ChatHub.OnConnectedAsync",
                        relatedEntityType: "SignalR",
                        additionalData: new { UserId = userId, ConversationId = conversationId, ConnectionId = Context.ConnectionId }
                    );
                }
                
                throw new HubException("Invalid user ID or conversation ID");
            }
        }
        catch (Exception ex)
        {
            // ✅ LOG EN BD: Error crítico de autenticación SignalR
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                await loggingService.LogErrorAsync(
                    message: "SignalR connection failed: Invalid token",
                    details: $"SignalR connection failed due to invalid token. Error: {ex.Message}, ConnectionId: {Context.ConnectionId}",
                    source: "ChatHub.OnConnectedAsync",
                    relatedEntityType: "SignalR",
                    additionalData: new { 
                        ConnectionId = Context.ConnectionId,
                        Error = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
            }
            
            throw new HubException($"Invalid token: {ex.Message}");
        }

        await base.OnConnectedAsync();
    }

    public async Task JoinConversation(int conversationId, int userId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"conversation-{conversationId}");
        // ✅ LOG EN BD: Usuario se unió a conversación
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
            await loggingService.LogInfoAsync(
                message: "User joined conversation via JoinConversation",
                details: $"User {userId} joined conversation {conversationId} via JoinConversation method. ConnectionId: {Context.ConnectionId}",
                userId: userId,
                source: "ChatHub.JoinConversation",
                relatedEntityType: "Conversation",
                relatedEntityId: conversationId,
                additionalData: new { ConnectionId = Context.ConnectionId, ConversationId = conversationId }
            );
        }
    }
}