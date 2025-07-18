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

namespace newApi.Controllers
{
    public class ChatHub : Hub
    {
        public async Task JoinConversation(int conversationId, int userId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"conversation-{conversationId}");
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
                Console.WriteLine("Claims received: " + string.Join(", ", claims.Select(c => $"{c.Type}: {c.Value}")));

                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }
                Console.WriteLine($"Parsed userId: {userId}");

                var conversation = await _context.Conversations
                    .Include(c => c.Messages)
                    .ThenInclude(m => m.Sender)
                    .Include(c => c.Client)
                    .Include(c => c.Expert)
                    .FirstOrDefaultAsync(c => c.SearchHire.SearchId == searchId &&
                                             (c.ClientId == userId || c.ExpertId == userId || User.IsInRole("Admin")));
                Console.WriteLine($"Existing conversation found: {conversation != null}");

                if (conversation == null)
                {
                    var searchHire = await _context.SearchHires
                        .Include(sh => sh.Search)
                        .FirstOrDefaultAsync(sh => sh.SearchId == searchId);
                    Console.WriteLine($"SearchHire found for searchId {searchId}: {searchHire != null}");

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
                        Console.WriteLine($"Authorization check failed - ClientId: {searchHire.ClientId}, ExpertId: {searchHire.ExpertId}, UserId: {userId}, IsAdmin: {User.IsInRole("Admin")}");
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
                    Console.WriteLine($"New conversation created with Id: {conversation.Id}");
                }

                var conversationDto = ConversationDto.FromConversation(conversation);
                return Ok(conversationDto);
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        // POST: api/chat/message
        [HttpPost("message")]
        public async Task<ActionResult<Message>> SendMessage([FromBody] SendMessageDto dto)
        {
            if (!int.TryParse(User.FindFirst("id")?.Value, out var userId))
            {
                return Unauthorized("Invalid or missing user ID in token");
            }

            var conversation = await _context.Conversations
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

            // Broadcast message to the conversation group via SignalR
            await _hubContext.Clients.Group($"conversation-{dto.ConversationId}")
                .SendAsync("ReceiveMessage", message);

            return Ok(message);
        }

        // PUT: api/chat/message/{messageId}/read
        [HttpPut("message/{messageId}/read")]
        public async Task<ActionResult> MarkMessageAsRead(int messageId)
        {
            if (!int.TryParse(User.FindFirst("id")?.Value, out var userId))
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

            // Notify the conversation group about the read status
            await _hubContext.Clients.Group($"conversation-{message.ConversationId}")
                .SendAsync("MessageRead", messageId);

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
                    SenderName = m.Sender?.Name // Include only necessary sender info
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