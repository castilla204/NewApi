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
using Google.Cloud.Storage.V1;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System.IO;
using System.Collections.Generic;
using System.Globalization;

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
        private readonly StorageClient _storageClient;
        private readonly IConfiguration _configuration;

        public ChatController(AppDbContext context, IHubContext<ChatHub> hubContext, StorageClient storageClient, IConfiguration configuration)
        {
            _context = context;
            _hubContext = hubContext;
            _storageClient = storageClient;
            _configuration = configuration;
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

                // Admin puede ver cualquier conversación, usuarios normales solo las suyas
                var conversation = await _context.Conversations
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Sender)
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Attachments)
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

                    // Verificar autorización: debe ser cliente, experto o admin
                    var isClient = searchHire.ClientId == userId;
                    var isExpert = searchHire.ExpertId == userId;
                    var isAdmin = User.IsInRole("Admin");
                    
                    if (!isClient && !isExpert && !isAdmin)
                    {
                        Console.WriteLine($"[13:42 CEST] Authorization check failed - ClientId: {searchHire.ClientId}, ExpertId: {searchHire.ExpertId}, UserId: {userId}, IsAdmin: {isAdmin}");
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

        /// <summary>
        /// Obtener todas las conversaciones (solo para Admin)
        /// </summary>
        [HttpGet("conversations")]
        [Authorize(Roles = "Admin")]
        public async Task<ActionResult<List<ConversationDto>>> GetAllConversations()
        {
            try
            {
                var conversations = await _context.Conversations
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Sender)
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Attachments)
                    .Include(c => c.Client)
                    .Include(c => c.Expert)
                    .Include(c => c.SearchHire)
                        .ThenInclude(sh => sh.Search)
                    .OrderByDescending(c => c.UpdatedAt)
                    .ToListAsync();

                var conversationDtos = conversations.Select(ConversationDto.FromConversation).ToList();
                return Ok(conversationDtos);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[13:42 CEST] Error getting all conversations: {ex.Message}");
                return StatusCode(500, new { message = "An error occurred while retrieving conversations" });
            }
        }

        /// <summary>
        /// Obtener conversación específica por ID (solo para Admin)
        /// </summary>
        [HttpGet("conversation/{conversationId}")]
        [Authorize(Roles = "Admin")]
        public async Task<ActionResult<ConversationDto>> GetConversationById(int conversationId)
        {
            try
            {
                var conversation = await _context.Conversations
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Sender)
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Attachments)
                    .Include(c => c.Client)
                    .Include(c => c.Expert)
                    .Include(c => c.SearchHire)
                        .ThenInclude(sh => sh.Search)
                    .FirstOrDefaultAsync(c => c.Id == conversationId);

                if (conversation == null)
                {
                    return NotFound(new { message = "Conversation not found" });
                }

                var conversationDto = ConversationDto.FromConversation(conversation);
                return Ok(conversationDto);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[13:42 CEST] Error getting conversation {conversationId}: {ex.Message}");
                return StatusCode(500, new { message = "An error occurred while retrieving conversation" });
            }
        }

        // ChatController.cs (SendMessage method)
        [HttpPost("message")]
        public async Task<ActionResult<MessageDto>> SendMessage([FromForm] SendMessageDto dto)
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                var conversation = await _context.Conversations
                    .Include(c => c.Messages)
                    .FirstOrDefaultAsync(c => c.Id == dto.ConversationId &&
                                             (c.ClientId == userId || c.ExpertId == userId));

                if (conversation == null)
                {
                    return NotFound(new { message = "Conversation not found or you are not authorized" });
                }

                if (string.IsNullOrEmpty(dto.Content) && (dto.Attachments == null || !dto.Attachments.Any()) &&
                    string.IsNullOrEmpty(dto.LocationLatitude) && string.IsNullOrEmpty(dto.LocationLongitude))
                {
                    return BadRequest(new { message = "At least one of content, attachments, or location must be provided" });
                }

                // Validate location data
                if (!string.IsNullOrEmpty(dto.LocationLatitude) && !string.IsNullOrEmpty(dto.LocationLongitude))
                {
                    // Use InvariantCulture to handle both comma and period separators
                    if (!double.TryParse(dto.LocationLatitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var lat) ||
                        !double.TryParse(dto.LocationLongitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var lon))
                    {
                        Console.WriteLine($"[20:34 CEST] Invalid latitude or longitude format: Latitude={dto.LocationLatitude}, Longitude={dto.LocationLongitude}");
                        return BadRequest(new { message = "Invalid latitude or longitude format" });
                    }
                    if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
                    {
                        Console.WriteLine($"[20:34 CEST] Latitude or longitude out of range: Latitude={lat}, Longitude={lon}");
                        return BadRequest(new { message = "Latitude must be between -90 and 90, and longitude between -180 and 180" });
                    }
                }

                var message = new Message
                {
                    ConversationId = dto.ConversationId,
                    SenderId = userId,
                    Content = string.IsNullOrEmpty(dto.Content) ? null : dto.Content, // Ensure nullable Content
                    SentAt = DateTime.UtcNow,
                    IsRead = false,
                    LocationLatitude = dto.LocationLatitude,
                    LocationLongitude = dto.LocationLongitude
                };

                _context.Messages.Add(message);
                conversation.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                var attachmentUrls = new List<string>();
                if (dto.Attachments != null && dto.Attachments.Any())
                {
                    var bucketName = _configuration["GoogleCloud:BucketName"];
                    foreach (var file in dto.Attachments)
                    {
                        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
                        if (!new[] { ".jpg", ".jpeg", ".png", ".mp4" }.Contains(extension))
                        {
                            return BadRequest(new { message = $"Invalid file type: {file.FileName}. Only JPG, PNG, and MP4 files are allowed" });
                        }

                        // Validate file size (10MB limit for messages)
                        if (file.Length > 10 * 1024 * 1024)
                        {
                            return BadRequest(new { message = $"File {file.FileName} exceeds 10MB limit" });
                        }

                        var uniqueFileName = $"{Guid.NewGuid()}{extension}";
                        var objectName = $"messages/{uniqueFileName}";
                        var contentType = extension == ".mp4" ? "video/mp4" : "image/jpeg";

                        try
                        {
                            using (var inputStream = file.OpenReadStream())
                            {
                                if (extension == ".jpg" || extension == ".jpeg" || extension == ".png")
                                {
                                    using (var image = Image.Load(inputStream))
                                    {
                                        image.Mutate(x => x.Resize(new ResizeOptions
                                        {
                                            Size = new Size(200, 200),
                                            Mode = ResizeMode.Max
                                        }));

                                        using (var outputStream = new MemoryStream())
                                        {
                                            image.SaveAsJpeg(outputStream);
                                            outputStream.Position = 0;
                                            await _storageClient.UploadObjectAsync(
                                                bucket: bucketName,
                                                objectName: objectName,
                                                contentType: contentType,
                                                source: outputStream
                                            );
                                        }
                                    }
                                }
                                else
                                {
                                    await _storageClient.UploadObjectAsync(
                                        bucket: bucketName,
                                        objectName: objectName,
                                        contentType: contentType,
                                        source: inputStream
                                    );
                                }
                            }

                            var attachmentUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";
                            attachmentUrls.Add(attachmentUrl);

                            var attachment = new MessageAttachment
                            {
                                MessageId = message.Id,
                                Url = attachmentUrl,
                                ObjectName = objectName,
                                Type = extension == ".mp4" ? "video" : "image",
                                CreatedAt = DateTime.UtcNow
                            };
                            _context.MessageAttachments.Add(attachment);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[20:34 CEST] Error uploading file {file.FileName}: {ex.Message}");
                            return StatusCode(500, new { message = $"Failed to upload file {file.FileName}: {ex.Message}" });
                        }
                    }
                    await _context.SaveChangesAsync();
                }

                var messageDto = new MessageDto
                {
                    Id = message.Id,
                    ConversationId = message.ConversationId,
                    SenderId = message.SenderId,
                    Content = message.Content,
                    SentAt = message.SentAt,
                    IsRead = message.IsRead,
                    SenderName = (await _context.Users.FindAsync(message.SenderId))?.Name,
                    LocationLatitude = message.LocationLatitude,
                    LocationLongitude = message.LocationLongitude,
                    AttachmentUrls = attachmentUrls
                };

                var options = new JsonSerializerOptions
                {
                    ReferenceHandler = ReferenceHandler.IgnoreCycles,
                    MaxDepth = 64
                };
                var serializedMessage = JsonSerializer.Serialize(messageDto, options);
                Console.WriteLine($"[20:34 CEST] Serialized message: {serializedMessage}");

                try
                {
                    await _hubContext.Clients.Group($"conversation-{dto.ConversationId}")
                        .SendAsync("ReceiveMessage", messageDto);
                    Console.WriteLine($"[20:34 CEST] Successfully broadcasted message {message.Id} to group conversation-{dto.ConversationId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[20:34 CEST] Failed to broadcast message {message.Id}: {ex.Message}");
                }

                return Ok(messageDto);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[20:34 CEST] Error in SendMessage: {ex.Message}\nStackTrace: {ex.StackTrace}");
                return StatusCode(500, new { message = "An error occurred while sending the message" });
            }
        }

        [HttpPut("message/{messageId}/read")]
        public async Task<ActionResult> MarkMessageAsRead(int messageId)
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                var message = await _context.Messages
                    .Include(m => m.Conversation)
                    .FirstOrDefaultAsync(m => m.Id == messageId &&
                                            (m.Conversation.ClientId == userId || m.Conversation.ExpertId == userId));

                if (message == null)
                {
                    return NotFound(new { message = "Message not found or you are not authorized" });
                }

                if (message.SenderId == userId)
                {
                    return BadRequest(new { message = "Cannot mark your own message as read" });
                }

                message.IsRead = true;
                await _context.SaveChangesAsync();

                try
                {
                    await _hubContext.Clients.Group($"conversation-{message.ConversationId}")
                        .SendAsync("MessageRead", messageId);
                    Console.WriteLine($"[20:08 CEST] Successfully broadcasted MessageRead for message {messageId} to group conversation-{message.ConversationId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[20:08 CEST] Failed to broadcast MessageRead for message {messageId}: {ex.Message}");
                }

                return Ok();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[20:08 CEST] Error in MarkMessageAsRead for message {messageId}: {ex.Message}\nStackTrace: {ex.StackTrace}");
                return StatusCode(500, new { message = "An error occurred while marking the message as read" });
            }
        }

        [HttpPost("deliverable/{searchHireId}")]
        public async Task<IActionResult> UploadDeliverable(int searchHireId, [FromForm] UploadDeliverableDto dto)
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                var searchHire = await _context.SearchHires
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId && sh.ExpertId == userId);
                if (searchHire == null)
                {
                    return NotFound(new { message = "SearchHire not found or you are not authorized to upload deliverables" });
                }

                var deliverableUrls = new List<string>();
                if (dto.Files != null && dto.Files.Any())
                {
                    var bucketName = _configuration["GoogleCloud:BucketName"];
                    foreach (var file in dto.Files)
                    {
                        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
                        if (!new[] { ".pdf", ".mp4" }.Contains(extension))
                        {
                            return BadRequest(new { message = "Only PDF and MP4 files are allowed" });
                        }

                        var uniqueFileName = $"{Guid.NewGuid()}{extension}";
                        var objectName = $"deliverables/{uniqueFileName}";
                        var contentType = extension == ".pdf" ? "application/pdf" : "video/mp4";

                        using (var inputStream = file.OpenReadStream())
                        {
                            await _storageClient.UploadObjectAsync(
                                bucket: bucketName,
                                objectName: objectName,
                                contentType: contentType,
                                source: inputStream
                            );
                        }

                        var deliverableUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";
                        deliverableUrls.Add(deliverableUrl);

                        var deliverable = new SearchHireDeliverable
                        {
                            SearchHireId = searchHireId,
                            Url = deliverableUrl,
                            ObjectName = objectName,
                            Type = extension == ".pdf" ? "pdf" : "video",
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.SearchHireDeliverables.Add(deliverable);
                    }
                    await _context.SaveChangesAsync();
                }

                var response = new DeliverableResponseDto
                {
                    SearchHireId = searchHireId,
                    DeliverableUrls = deliverableUrls,
                    CreatedAt = DateTime.UtcNow
                };

                try
                {
                    var conversation = await _context.Conversations
                        .FirstOrDefaultAsync(c => c.SearchHireId == searchHireId);
                    if (conversation != null)
                    {
                        await _hubContext.Clients.Group($"conversation-{conversation.Id}")
                            .SendAsync("ReceiveDeliverable", response);
                        Console.WriteLine($"[13:42 CEST] Successfully broadcasted deliverable for SearchHire {searchHireId} to group conversation-{conversation.Id}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[13:42 CEST] Failed to broadcast deliverable for SearchHire {searchHireId}: {ex.Message}");
                }

                return Ok(new { message = "Deliverable uploaded successfully", deliverable = response });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[13:42 CEST] Error uploading deliverable for SearchHire {searchHireId}: {ex.Message}");
                return StatusCode(500, new { message = "An error occurred while uploading the deliverable" });
            }
        }

        [HttpGet("deliverable/{searchHireId}")]
        public async Task<IActionResult> GetDeliverables(int searchHireId)
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                var searchHire = await _context.SearchHires
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId && (sh.ClientId == userId || sh.ExpertId == userId || User.IsInRole("Admin")));
                if (searchHire == null)
                {
                    return NotFound(new { message = "SearchHire not found or you are not authorized" });
                }

                var deliverables = await _context.SearchHireDeliverables
                    .Where(d => d.SearchHireId == searchHireId)
                    .Select(d => new DeliverableResponseDto
                    {
                        SearchHireId = d.SearchHireId,
                        DeliverableUrls = new List<string> { d.Url },
                        CreatedAt = d.CreatedAt
                    })
                    .ToListAsync();

                if (!deliverables.Any())
                {
                    return Ok(new { message = "No deliverables found for this SearchHire", deliverables = new List<DeliverableResponseDto>() });
                }

                var combinedResponse = new DeliverableResponseDto
                {
                    SearchHireId = searchHireId,
                    DeliverableUrls = deliverables.SelectMany(d => d.DeliverableUrls).ToList(),
                    CreatedAt = deliverables.Max(d => d.CreatedAt)
                };

                return Ok(new { message = "Deliverables retrieved successfully", deliverable = combinedResponse });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[13:42 CEST] Error retrieving deliverables for SearchHire {searchHireId}: {ex.Message}");
                return StatusCode(500, new { message = "An error occurred while retrieving deliverables" });
            }
        }
    }

    public class SendMessageDto
    {
        public int ConversationId { get; set; }
        public string? Content { get; set; } 
        public string? LocationLatitude { get; set; }
        public string? LocationLongitude { get; set; }
        public List<IFormFile>? Attachments { get; set; }
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
                    SenderName = m.Sender?.Name,
                    LocationLatitude = m.LocationLatitude,
                    LocationLongitude = m.LocationLongitude,
                    AttachmentUrls = m.Attachments.Select(a => a.Url).ToList()
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
        public string? LocationLatitude { get; set; }
        public string? LocationLongitude { get; set; }
        public List<string> AttachmentUrls { get; set; } = new List<string>();
    }

    public class UploadDeliverableDto
    {
        public List<IFormFile> Files { get; set; }
    }

    public class DeliverableResponseDto
    {
        public int SearchHireId { get; set; }
        public List<string> DeliverableUrls { get; set; } = new List<string>();
        public DateTime CreatedAt { get; set; }
    }
}