using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using newApi.Services;
using newApi.DataLayer.Models;
using System.Collections.Concurrent;
using System.Security.Claims;

public class ChatHub : Hub
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private static readonly ConcurrentDictionary<string, int> _connectionUsers = new();

    public ChatHub(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    public override async Task OnConnectedAsync()
    {
        if (!(Context.User?.Identity?.IsAuthenticated ?? false))
        {
            await LogWarningAsync("SignalR connection failed: unauthenticated user",
                new { ConnectionId = Context.ConnectionId });
            throw new HubException("Authentication required");
        }

        if (!int.TryParse(Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            await LogWarningAsync("SignalR connection failed: missing user identifier",
                new { ConnectionId = Context.ConnectionId });
            throw new HubException("Invalid user context");
        }

        _connectionUsers[Context.ConnectionId] = userId;
        await LogInfoAsync("User connected to SignalR", userId, null,
            new { ConnectionId = Context.ConnectionId });

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_connectionUsers.TryRemove(Context.ConnectionId, out var userId))
        {
            await LogInfoAsync("User disconnected from SignalR", userId, null,
                new { ConnectionId = Context.ConnectionId, Error = exception?.Message });
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinConversation(int conversationId)
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
        var isAdmin = authService.IsAdmin(Context.User);

        if (!isClient && !isExpert && !isAdmin)
        {
            await LogWarningAsync("Unauthorized attempt to join conversation",
                new { ConnectionId = Context.ConnectionId, ConversationId = conversationId, UserId = userId });
            throw new HubException("You are not authorized to join this conversation");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"conversation-{conversationId}");

        await LogInfoAsync("User joined conversation via SignalR", userId, conversationId,
            new { ConnectionId = Context.ConnectionId, ConversationId = conversationId });
    }

    public async Task LeaveConversation(int conversationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"conversation-{conversationId}");
        var userId = _connectionUsers.TryGetValue(Context.ConnectionId, out var value) ? value : (int?)null;
        await LogInfoAsync("User left conversation via SignalR", userId, conversationId,
            new { ConnectionId = Context.ConnectionId, ConversationId = conversationId });
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
        using var scope = _serviceScopeFactory.CreateScope();
        var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
        await loggingService.LogInfoAsync(
            message: message,
            details: message,
            userId: userId,
            source: "ChatHub",
            relatedEntityType: conversationId.HasValue ? "Conversation" : "SignalR",
            relatedEntityId: conversationId,
            additionalData: additionalData
        );
    }

    private async Task LogWarningAsync(string message, object additionalData)
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
}