using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

public class ChatHub : Hub
{
    private readonly IConfiguration _configuration;

    public ChatHub(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public override async Task OnConnectedAsync()
    {
        var token = Context.GetHttpContext()?.Request.Query["access_token"].ToString();
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("[13:40 CEST] Connection failed: Missing authentication token");
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
                Console.WriteLine($"[13:40 CEST] User {userId} joined conversation {conversationId} on connect, Connection ID: {Context.ConnectionId}");
            }
            else
            {
                Console.WriteLine($"[13:40 CEST] Connection failed: Invalid user ID ({userId}) or conversation ID ({conversationId})");
                throw new HubException("Invalid user ID or conversation ID");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[13:40 CEST] Connection failed: Invalid token: {ex.Message}");
            throw new HubException($"Invalid token: {ex.Message}");
        }

        await base.OnConnectedAsync();
    }

    public async Task JoinConversation(int conversationId, int userId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"conversation-{conversationId}");
        Console.WriteLine($"[13:40 CEST] User {userId} joined conversation {conversationId} via JoinConversation, Connection ID: {Context.ConnectionId}");
    }
}