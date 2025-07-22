using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using Microsoft.AspNetCore.Authorization;
using System.Threading.Tasks;
using System.Linq;
using System;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace newApi.Controllers
{
    public class ChatHub : Hub
    {
        public async Task JoinConversation(int conversationId, int userId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"conversation-{conversationId}");
            Console.WriteLine($"[13:42 CEST] User {userId} joined conversation {conversationId} via JoinConversation, Connection ID: {Context.ConnectionId}");
        }
    }

    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ChatController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IHubContext<ChatHub> _hubContext;

        public ChatController(AppDbContext context, IHubContext<ChatHub> hubContext)
        {
            _context = context;
            _hubContext = hubContext;
        }

        [HttpGet("conversation")]
        public async Task<ActionResult<ConversationDto>> GetConversation([FromQuery] int searchId)
        {
            try
            {
                var claims = User.Claims.Select(c => new { c.Type, c.Value }).ToList();
                Console.WriteLine("[13:42 CEST] Claims received: " + string.Join(", ", claims.Select(c => $"{c.Type}: {c.Value}")));

                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }
                Console.WriteLine($"[13:42 CEST] Parsed userId: {userId}");

                var conversation = await _context.Conversations
                    .Include(c => c.Messages)
                    .ThenInclude(m => m.Sender)
                    .Include(c => c.Client)
                    .Include(c => c.Expert)
                    .FirstOrDefaultAsync(c => c.SearchHire.SearchId == searchId &&
                                             (c.ClientId == userId || c.ExpertId == userId || User.IsInRole("Admin")));
                Console.WriteLine($"[13:42 CEST] Existing conversation found: {conversation != null}");

                if (conversation == null)
                {
                    var searchHire = await _context.SearchHires
                        .Include(sh => sh.Search)
                        .FirstOrDefaultAsync(sh => sh.SearchId == searchId);
                    Console.WriteLine($"[13:42 CEST] SearchHire found for searchId {searchId}: {searchHire != null}");

                    if (searchHire == null)
                    {
                        return NotFound(new { message = "Search hire not found" });
                    }

                    if (!searchHire.ExpertId.HasValue)
                    {
                        return BadRequest(new { message = "Cannot create conversation: No expert assigned to this search hire" });
                    }

                    if (searchHire.ClientId != userId && searchHire.ExpertId != userId && !User.IsInRole("Admin"))
                    {
                        Console.WriteLine($"[13:42 CEST] Authorization check failed - ClientId: {searchHire.ClientId}, ExpertId: {searchHire.ExpertId}, UserId: {userId}, IsAdmin: {User.IsInRole("Admin")}");
                        return Unauthorized(new { message = "You are not authorized to create a conversation for this search" });
                    }

                    conversation = new Conversation
                    {
                        SearchHireId = searchHire.Id,
                        ClientId = searchHire.ClientId,
                        ExpertId = searchHire.ExpertId.Value,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        Messages = new List<Message>()
                    };

                    _context.Conversations.Add(conversation);
                    await _context.SaveChangesAsync();
                    Console.WriteLine($"[13:42 CEST] New conversation created with Id: {conversation.Id}");
                }

                var conversationDto = ConversationDto.FromConversation(conversation);
                return Ok(conversationDto);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[13:42 CEST] Error in GetConversation: {ex.Message}");
                throw;
            }
        }

        [HttpPost("message")]
        public async Task<ActionResult<Message>> SendMessage([FromBody] SendMessageDto dto)
        {
            if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
            {
                return Unauthorized("Invalid or missing user ID in token");
            }

            var conversation = await _context.Conversations
                .Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == dto.ConversationId &&
                                       (c.ClientId == userId || c.ExpertId == userId));

            if (conversation == null)
            {
                return NotFound("Conversation not found or you are not authorized");
            }

            var message = new Message
            {
                ConversationId = dto.ConversationId,
                SenderId = userId,
                Content = dto.Content,
                SentAt = DateTime.UtcNow,
                IsRead = false
            };

            _context.Messages.Add(message);
            conversation.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var options = new JsonSerializerOptions
            {
                ReferenceHandler = ReferenceHandler.IgnoreCycles,
                MaxDepth = 64
            };
            var serializedMessage = JsonSerializer.Serialize(message, options);
            Console.WriteLine($"[13:42 CEST] Serialized message: {serializedMessage}");
            var broadcastMessage = JsonSerializer.Deserialize<Message>(serializedMessage, options);
            Console.WriteLine($"[13:42 CEST] Broadcasting message to group conversation-{dto.ConversationId}: {JsonSerializer.Serialize(broadcastMessage)}");

            try
            {
                var groupClients = _hubContext.Clients.Group($"conversation-{dto.ConversationId}");
                await groupClients.SendAsync("ReceiveMessage", broadcastMessage);
                Console.WriteLine($"[13:42 CEST] Successfully broadcasted message {message.Id} to group conversation-{dto.ConversationId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[13:42 CEST] Failed to broadcast message {message.Id}: {ex.Message}");
            }

            return Ok(broadcastMessage);
        }

        [HttpPut("message/{messageId}/read")]
        public async Task<ActionResult> MarkMessageAsRead(int messageId)
        {
            if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
            {
                return Unauthorized("Invalid or missing user ID in token");
            }

            var message = await _context.Messages
                .Include(m => m.Conversation)
                .FirstOrDefaultAsync(m => m.Id == messageId &&
                                        (m.Conversation.ClientId == userId || m.Conversation.ExpertId == userId));

            if (message == null)
            {
                return NotFound("Message not found or you are not authorized");
            }

            if (message.SenderId == userId)
            {
                return BadRequest("Cannot mark your own message as read");
            }

            message.IsRead = true;
            await _context.SaveChangesAsync();

            try
            {
                await _hubContext.Clients.Group($"conversation-{message.ConversationId}")
                    .SendAsync("MessageRead", messageId);
                Console.WriteLine($"[13:42 CEST] Successfully broadcasted MessageRead for message {messageId} to group conversation-{message.ConversationId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[13:42 CEST] Failed to broadcast MessageRead for message {messageId}: {ex.Message}");
            }

            return Ok();
        }
    }

    public class SendMessageDto
    {
        public int ConversationId { get; set; }
        public string Content { get; set; }
    }

    public class ConversationDto
    {
        public int Id { get; set; }
        public int SearchHireId { get; set; }
        public int ClientId { get; set; }
        public int? ExpertId { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<MessageDto> Messages { get; set; } = new List<MessageDto>();

        public static ConversationDto FromConversation(Conversation conversation)
        {
            return new ConversationDto
            {
                Id = conversation.Id,
                SearchHireId = conversation.SearchHireId,
                ClientId = conversation.ClientId,
                ExpertId = conversation.ExpertId,
                IsActive = conversation.IsActive,
                CreatedAt = conversation.CreatedAt,
                UpdatedAt = conversation.UpdatedAt,
                Messages = conversation.Messages.Select(m => new MessageDto
                {
                    Id = m.Id,
                    ConversationId = m.ConversationId,
                    SenderId = m.SenderId,
                    Content = m.Content,
                    SentAt = m.SentAt,
                    IsRead = m.IsRead,
                    SenderName = m.Sender?.Name
                }).ToList()
            };
        }
    }

    public class MessageDto
    {
        public int Id { get; set; }
        public int ConversationId { get; set; }
        public int SenderId { get; set; }
        public string Content { get; set; }
        public DateTime SentAt { get; set; }
        public bool IsRead { get; set; }
        public string SenderName { get; set; }
    }
}