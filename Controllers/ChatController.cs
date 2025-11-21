using Microsoft.AspNetCore.Http;
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
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using newApi.Services;

namespace newApi.Controllers
{
    // ✅ REMOVED: Duplicate ChatHub class - using the one in ChatHub.cs file

    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ChatController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly StorageClient _storageClient;
        private readonly IConfiguration _configuration;
        private readonly IAuthorizationServices _authService;
        private readonly ILoggingService _loggingService;
        private readonly ISignedUrlService _signedUrlService;
        private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };
        private static readonly HashSet<string> AllowedVideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4" };
        private static readonly HashSet<string> AllowedImageContentTypes = new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png" };
        private static readonly HashSet<string> AllowedVideoContentTypes = new(StringComparer.OrdinalIgnoreCase) { "video/mp4" };
        private const long MaxAttachmentSizeBytes = 10 * 1024 * 1024;

        public ChatController(
            AppDbContext context,
            IHubContext<ChatHub> hubContext,
            StorageClient storageClient,
            IConfiguration configuration,
            IAuthorizationServices authService,
            ILoggingService loggingService,
            ISignedUrlService signedUrlService)
        {
            _context = context;
            _hubContext = hubContext;
            _storageClient = storageClient;
            _configuration = configuration;
            _authService = authService;
            _loggingService = loggingService;
            _signedUrlService = signedUrlService;
        }

        [HttpGet("conversation")]
        public async Task<ActionResult<ConversationDto>> GetConversation([FromQuery] int searchId)
        {
            try
            {
                var claims = User.Claims.Select(c => new { c.Type, c.Value }).ToList();

                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }
                // Admin puede ver cualquier conversación, usuarios normales solo las suyas
                var conversation = await _context.Conversations
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Sender)
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Attachments)
                    .Include(c => c.Client)
                    .Include(c => c.Expert)
                    .FirstOrDefaultAsync(c => c.SearchHire.SearchId == searchId &&
                                             ((c.ClientId.HasValue && c.ClientId.Value == userId) || 
                                              (c.ExpertId.HasValue && c.ExpertId.Value == userId) || 
                                              _authService.IsAdmin(User)));
                if (conversation == null)
                {
                    var searchHire = await _context.SearchHires
                        .Include(sh => sh.Search)
                        .FirstOrDefaultAsync(sh => sh.SearchId == searchId);
                    if (searchHire == null)
                    {
                        return NotFound(new { message = "Search hire not found" });
                    }

                    // Verificar autorización: debe ser cliente, experto o admin
                    // ✅ MEJORA: Manejar nullable ClientId y ExpertId correctamente
                    var isClient = searchHire.ClientId.HasValue && searchHire.ClientId.Value == userId;
                    var isExpert = searchHire.ExpertId.HasValue && searchHire.ExpertId.Value == userId;
                    var isAdmin = _authService.IsAdmin(User);
                    
                    if (!isClient && !isExpert && !isAdmin)
                    {
                        return Unauthorized(new { message = "You are not authorized to create a conversation for this search" });
                    }

                    // ✅ CORRECCIÓN: Permitir crear conversación incluso si ExpertId es NULL (experto borró cuenta)
                    // La conversación debe preservarse para que el cliente pueda ver el historial
                    conversation = new Conversation
                    {
                        SearchHireId = searchHire.Id,
                        ClientId = searchHire.ClientId, // ✅ ClientId es ahora nullable, asignación directa
                        ExpertId = searchHire.ExpertId, // ✅ ExpertId es nullable, puede ser NULL si experto borró cuenta
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        Messages = new List<Message>()
                    };

                    _context.Conversations.Add(conversation);
                    await _context.SaveChangesAsync();
                    
                    // ✅ LOG INFORMATIVO: Nueva conversación creada
                    await _loggingService.LogInfoAsync(
                        message: "New conversation created",
                        details: $"New conversation created for searchId {searchId}. ConversationId: {conversation.Id}, ClientId: {conversation.ClientId}, ExpertId: {conversation.ExpertId}",
                        userId: userId,
                        source: "ChatController.GetConversation",
                        relatedEntityType: "Conversation",
                        relatedEntityId: conversation.Id,
                        additionalData: new { 
                            Action = "CreateConversation",
                            SearchId = searchId,
                            ConversationId = conversation.Id,
                            ClientId = conversation.ClientId,
                            ExpertId = conversation.ExpertId
                        }
                    );
                }

                var conversationDto = ConversationDto.FromConversation(conversation);
                PopulateSignedAttachmentUrls(conversation, conversationDto);
                return Ok(conversationDto);
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <summary>
        /// Obtener todas las conversaciones (solo para Admin)
        /// </summary>
        [HttpGet("conversations")]
        public async Task<ActionResult<List<ConversationDto>>> GetAllConversations()
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
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

                var conversationDtos = conversations
                    .Select(c => ConversationDto.FromConversation(c))
                    .ToList();

                for (var i = 0; i < conversations.Count && i < conversationDtos.Count; i++)
                {
                    PopulateSignedAttachmentUrls(conversations[i], conversationDtos[i]);
                }

                return Ok(conversationDtos);
            }
            catch (Exception ex)
            {
                // ✅ LOG EN BD: Error al obtener conversaciones
                await _loggingService.LogErrorAsync(
                    message: "Error getting all conversations",
                    details: $"Error retrieving all conversations: {ex.Message}",
                    source: "ChatController.GetAllConversations",
                    relatedEntityType: "Conversation",
                    additionalData: new { 
                        Exception = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                
                return StatusCode(500, new { message = "An error occurred while retrieving conversations" });
            }
        }

        /// <summary>
        /// Obtener conversación directamente por SearchHireId
        /// Funciona incluso cuando el Search fue eliminado (cliente borró su cuenta)
        /// </summary>
        [HttpGet("by-searchhire/{searchHireId}")]
        public async Task<ActionResult<ConversationDto>> GetConversationBySearchHireId(int searchHireId)
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                // Buscar conversación directamente por SearchHireId (no depende de SearchId)
                var conversation = await _context.Conversations
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Sender)
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Attachments)
                    .Include(c => c.Client)
                    .Include(c => c.Expert)
                    .Include(c => c.SearchHire)
                    .FirstOrDefaultAsync(c => c.SearchHireId == searchHireId &&
                                             ((c.ClientId.HasValue && c.ClientId.Value == userId) || 
                                              (c.ExpertId.HasValue && c.ExpertId.Value == userId) || 
                                              _authService.IsAdmin(User)));

                if (conversation == null)
                {
                    // Verificar si el SearchHire existe y crear conversación si no existe
                    var searchHire = await _context.SearchHires
                        .FirstOrDefaultAsync(sh => sh.Id == searchHireId);

                    if (searchHire == null)
                    {
                        return NotFound(new { message = "Search hire not found" });
                    }

                    // Verificar autorización
                    var isClient = searchHire.ClientId.HasValue && searchHire.ClientId.Value == userId;
                    var isExpert = searchHire.ExpertId.HasValue && searchHire.ExpertId.Value == userId;
                    var isAdmin = _authService.IsAdmin(User);
                    
                    if (!isClient && !isExpert && !isAdmin)
                    {
                        return Unauthorized(new { message = "You are not authorized to access this conversation" });
                    }

                    // ✅ CORRECCIÓN: Permitir crear conversación incluso si ExpertId es NULL (experto borró cuenta)
                    // La conversación debe preservarse para que el cliente pueda ver el historial
                    // Crear nueva conversación
                    conversation = new Conversation
                    {
                        SearchHireId = searchHireId,
                        ClientId = searchHire.ClientId,
                        ExpertId = searchHire.ExpertId, // ✅ Puede ser NULL si experto borró cuenta
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        Messages = new List<Message>()
                    };

                    _context.Conversations.Add(conversation);
                    await _context.SaveChangesAsync();
                    
                    await _loggingService.LogInfoAsync(
                        message: "New conversation created by SearchHireId",
                        details: $"New conversation created for SearchHireId {searchHireId}. ConversationId: {conversation.Id}, ClientId: {conversation.ClientId}, ExpertId: {conversation.ExpertId}",
                        userId: userId,
                        source: "ChatController.GetConversationBySearchHireId",
                        relatedEntityType: "Conversation",
                        relatedEntityId: conversation.Id,
                        additionalData: new { 
                            Action = "CreateConversation",
                            SearchHireId = searchHireId,
                            ConversationId = conversation.Id,
                            ClientId = conversation.ClientId,
                            ExpertId = conversation.ExpertId
                        }
                    );
                }

                var conversationDto = ConversationDto.FromConversation(conversation);
                PopulateSignedAttachmentUrls(conversation, conversationDto);
                return Ok(conversationDto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to retrieve conversation", detail = ex.Message });
            }
        }

        public async Task<ActionResult<ConversationDto>> GetConversationById(int conversationId)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
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
                PopulateSignedAttachmentUrls(conversation, conversationDto);
                return Ok(conversationDto);
            }
            catch (Exception ex)
            {
                // ✅ LOG EN BD: Error al obtener conversación
                await _loggingService.LogErrorAsync(
                    message: "Error getting conversation by ID",
                    details: $"Error retrieving conversation {conversationId}: {ex.Message}",
                    source: "ChatController.GetConversationById",
                    relatedEntityType: "Conversation",
                    relatedEntityId: conversationId,
                    additionalData: new { 
                        ConversationId = conversationId,
                        Exception = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                
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

                var isAdmin = _authService.IsAdmin(User);
                var conversation = await _context.Conversations
                    .Include(c => c.Messages)
                    .FirstOrDefaultAsync(c => c.Id == dto.ConversationId);

                if (conversation == null)
                {
                    return NotFound(new { message = "Conversation not found" });
                }

                if (!UserBelongsToConversation(conversation, userId, isAdmin))
                {
                    return Unauthorized(new { message = "You are not authorized to send messages to this conversation" });
                }

                var sanitizedContent = SanitizeMessageContent(dto.Content);
                var hasAttachments = dto.Attachments != null && dto.Attachments.Any();
                var hasLocation = !string.IsNullOrWhiteSpace(dto.LocationLatitude) && !string.IsNullOrWhiteSpace(dto.LocationLongitude);

                if (string.IsNullOrEmpty(sanitizedContent) && !hasAttachments && !hasLocation)
                {
                    return BadRequest(new { message = "At least one of content, attachments, or location must be provided" });
                }

                // Validate location data
                if (hasLocation)
                {
                    // Use InvariantCulture to handle both comma and period separators
                    if (!double.TryParse(dto.LocationLatitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var lat) ||
                        !double.TryParse(dto.LocationLongitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var lon))
                    {
                        return BadRequest(new { message = "Invalid latitude or longitude format" });
                    }
                    if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
                    {
                        return BadRequest(new { message = "Latitude must be between -90 and 90, and longitude between -180 and 180" });
                    }
                }

                var message = new Message
                {
                    ConversationId = dto.ConversationId,
                    SenderId = userId,
                    Content = string.IsNullOrEmpty(sanitizedContent) ? null : sanitizedContent,
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
                        try
                        {
                            var uploadResult = await ValidateAndUploadAttachmentAsync(file, bucketName, dto.ConversationId, userId);

                            var attachment = new MessageAttachment
                            {
                                MessageId = message.Id,
                                Url = uploadResult.Url,
                                ObjectName = uploadResult.ObjectName,
                                Type = uploadResult.Type,
                                CreatedAt = DateTime.UtcNow
                            };
                            _context.MessageAttachments.Add(attachment);
                            attachmentUrls.Add(ResolveAttachmentUrl(attachment));
                        }
                        catch (InvalidOperationException ex)
                        {
                            return BadRequest(new { message = ex.Message });
                        }
                        catch (Exception)
                        {
                            return StatusCode(500, new { message = $"Failed to upload file {file.FileName}" });
                        }
                    }
                    await _context.SaveChangesAsync();
                }

                // ✅ MEJORA: Manejar SenderId nullable y obtener nombre del sender si existe
                string? senderName = null;
                if (message.SenderId.HasValue)
                {
                    var sender = await _context.Users
                        .IgnoreQueryFilters() // Ignorar query filter para poder acceder a usuarios eliminados si es necesario
                        .FirstOrDefaultAsync(u => u.Id == message.SenderId.Value);
                    senderName = sender?.Name ?? "[Usuario eliminado]";
                }
                else
                {
                    senderName = "[Usuario eliminado]";
                }
                
                var messageDto = new MessageDto
                {
                    Id = message.Id,
                    ConversationId = message.ConversationId,
                    SenderId = message.SenderId, // ✅ Ahora es nullable, asignación directa
                    Content = message.Content ?? "[Mensaje eliminado]",
                    SentAt = message.SentAt,
                    IsRead = message.IsRead,
                    SenderName = senderName ?? "[Usuario eliminado]",
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
                try
                {
                    await _hubContext.Clients.Group($"conversation-{dto.ConversationId}")
                        .SendAsync("ReceiveMessage", messageDto);
                }
                catch (Exception)
                {
                    // Silently ignore SignalR errors - message is already saved
                }

                return Ok(messageDto);
            }
            catch (Exception ex)
            {
                // ✅ LOG EN BD: Error al enviar mensaje
                var userIdForLog = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int userIdValue) ? userIdValue : (int?)null;
                await _loggingService.LogErrorAsync(
                    message: "Error sending message",
                    details: $"Error sending message in conversation {dto.ConversationId}: {ex.Message}",
                    userId: userIdForLog,
                    source: "ChatController.SendMessage",
                    relatedEntityType: "Message",
                    relatedEntityId: dto.ConversationId,
                    additionalData: new { 
                        ConversationId = dto.ConversationId,
                        Exception = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                
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
                    .FirstOrDefaultAsync(m => m.Id == messageId);

                if (message == null)
                {
                    return NotFound(new { message = "Message not found" });
                }

                var isAdmin = _authService.IsAdmin(User);
                if (message.Conversation == null || !UserBelongsToConversation(message.Conversation, userId, isAdmin))
                {
                    return Unauthorized(new { message = "You are not authorized to read this message" });
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
                }
                catch (Exception)
                {
                    // Silently ignore SignalR errors - message is already saved
                }

                return Ok();
            }
            catch (Exception ex)
            {
                // ✅ LOG EN BD: Error al marcar mensaje como leído
                var userIdForLog = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int userIdValue) ? userIdValue : (int?)null;
                await _loggingService.LogErrorAsync(
                    message: "Error marking message as read",
                    details: $"Error marking message {messageId} as read: {ex.Message}",
                    userId: userIdForLog,
                    source: "ChatController.MarkMessageAsRead",
                    relatedEntityType: "Message",
                    relatedEntityId: messageId,
                    additionalData: new { 
                        MessageId = messageId,
                        Exception = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                
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
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId && sh.ExpertId.HasValue && sh.ExpertId.Value == userId);
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
                                source: inputStream,
                                options: new UploadObjectOptions
                                {
                                    PredefinedAcl = PredefinedObjectAcl.Private
                                }
                            );
                        }

                        var deliverableUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";

                        var deliverable = new SearchHireDeliverable
                        {
                            SearchHireId = searchHireId,
                            Url = deliverableUrl,
                            ObjectName = objectName,
                            Type = extension == ".pdf" ? "pdf" : "video",
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.SearchHireDeliverables.Add(deliverable);
                        deliverableUrls.Add(ResolveDeliverableUrl(deliverable));
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
                    }
                }
                catch (Exception)
                {
                    // Silently ignore SignalR errors - message is already saved
                }

                return Ok(new { message = "Deliverable uploaded successfully", deliverable = response });
            }
            catch (Exception ex)
            {
                // ✅ LOG EN BD: Error al subir entregable
                var userIdForLog = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int userIdValue) ? userIdValue : (int?)null;
                await _loggingService.LogErrorAsync(
                    message: "Error uploading deliverable",
                    details: $"Error uploading deliverable for SearchHire {searchHireId}: {ex.Message}",
                    userId: userIdForLog,
                    source: "ChatController.UploadDeliverable",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new { 
                        SearchHireId = searchHireId,
                        Exception = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                
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
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId && 
                                             ((sh.ClientId.HasValue && sh.ClientId.Value == userId) || 
                                              (sh.ExpertId.HasValue && sh.ExpertId.Value == userId) || 
                                              _authService.IsAdmin(User)));
                if (searchHire == null)
                {
                    return NotFound(new { message = "SearchHire not found or you are not authorized" });
                }

                    var deliverableEntities = await _context.SearchHireDeliverables
                        .Where(d => d.SearchHireId == searchHireId)
                        .ToListAsync();

                    var deliverables = deliverableEntities
                        .Select(d => new DeliverableResponseDto
                        {
                            SearchHireId = d.SearchHireId,
                            DeliverableUrls = new List<string> { ResolveDeliverableUrl(d) },
                            CreatedAt = d.CreatedAt
                        })
                        .ToList();

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
                // ✅ LOG EN BD: Error al obtener entregables
                var userIdForLog = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int userIdValue) ? userIdValue : (int?)null;
                await _loggingService.LogErrorAsync(
                    message: "Error retrieving deliverables",
                    details: $"Error retrieving deliverables for SearchHire {searchHireId}: {ex.Message}",
                    userId: userIdForLog,
                    source: "ChatController.GetDeliverables",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new { 
                        SearchHireId = searchHireId,
                        Exception = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                
                return StatusCode(500, new { message = "An error occurred while retrieving deliverables" });
            }
        }

        private bool UserBelongsToConversation(Conversation conversation, int userId, bool isAdmin)
        {
            if (isAdmin)
            {
                return true;
            }

            if (conversation == null)
            {
                return false;
            }

            if (conversation.ClientId.HasValue && conversation.ClientId.Value == userId)
            {
                return true;
            }

            if (conversation.ExpertId.HasValue && conversation.ExpertId.Value == userId)
            {
                return true;
            }

            return false;
        }

        private static string SanitizeMessageContent(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            var trimmed = content.Trim();
            return Regex.Replace(trimmed, @"[\u0000-\u001F\u007F]", string.Empty);
        }

        private async Task<(string Url, string ObjectName, string Type)> ValidateAndUploadAttachmentAsync(
            IFormFile file,
            string bucketName,
            int conversationId,
            int userId)
        {
            if (file == null || file.Length == 0)
            {
                throw new InvalidOperationException("Attachment is empty");
            }

            if (file.Length > MaxAttachmentSizeBytes)
            {
                throw new InvalidOperationException($"File {file.FileName} exceeds 10MB limit");
            }

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var contentType = (file.ContentType ?? string.Empty).ToLowerInvariant();

            var isImage = AllowedImageExtensions.Contains(extension) && AllowedImageContentTypes.Contains(contentType);
            var isVideo = AllowedVideoExtensions.Contains(extension) && AllowedVideoContentTypes.Contains(contentType);

            if (!isImage && !isVideo)
            {
                throw new InvalidOperationException($"Unsupported file type: {file.FileName}");
            }

            var uniqueFileName = $"{Guid.NewGuid()}{extension}";
            var objectName = $"messages/{uniqueFileName}";

            try
            {
                using var inputStream = file.OpenReadStream();
                await _storageClient.UploadObjectAsync(
                    bucket: bucketName,
                    objectName: objectName,
                    contentType: contentType,
                    source: inputStream,
                    options: new UploadObjectOptions
                    {
                        PredefinedAcl = PredefinedObjectAcl.Private
                    });
            }
            catch (Exception ex)
            {
                await _loggingService.LogErrorAsync(
                    message: "Error uploading file for message",
                    details: $"Error uploading file {file.FileName} for conversation {conversationId}: {ex.Message}",
                    userId: userId,
                    source: "ChatController.SendMessage",
                    relatedEntityType: "Message",
                    relatedEntityId: conversationId,
                    additionalData: new
                    {
                        FileName = file.FileName,
                        FileSize = file.Length,
                        ConversationId = conversationId,
                        Exception = ex.Message,
                        StackTrace = ex.StackTrace
                    });

                throw;
            }

            var url = $"https://storage.googleapis.com/{bucketName}/{objectName}";
            return (url, objectName, isVideo ? "video" : "image");
        }

        private string ResolveAttachmentUrl(MessageAttachment? attachment)
        {
            if (attachment == null)
            {
                return string.Empty;
            }

            var fallback = string.IsNullOrWhiteSpace(attachment.Url) ? string.Empty : attachment.Url;
            return _signedUrlService.GetSignedUrl(attachment.ObjectName ?? string.Empty) ?? fallback;
        }

        private string ResolveDeliverableUrl(SearchHireDeliverable? deliverable)
        {
            if (deliverable == null)
            {
                return string.Empty;
            }

            var fallback = string.IsNullOrWhiteSpace(deliverable.Url) ? string.Empty : deliverable.Url;
            return _signedUrlService.GetSignedUrl(deliverable.ObjectName ?? string.Empty) ?? fallback;
        }

        private void PopulateSignedAttachmentUrls(Conversation conversation, ConversationDto conversationDto)
        {
            if (conversation?.Messages == null || conversationDto?.Messages == null)
            {
                return;
            }

            var messageLookup = conversation.Messages.ToDictionary(m => m.Id);
            foreach (var messageDto in conversationDto.Messages)
            {
                if (messageLookup.TryGetValue(messageDto.Id, out var message))
                {
                    messageDto.AttachmentUrls = message.Attachments?
                        .Select(ResolveAttachmentUrl)
                        .Where(url => !string.IsNullOrEmpty(url))
                        .ToList() ?? new List<string>();
                }
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
        public int? ClientId { get; set; } // ✅ Nullable para permitir anonimización completa
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
                    SenderId = m.SenderId, // ✅ Ahora es nullable, asignación directa
                    Content = m.Content ?? "[Mensaje eliminado]",
                    SentAt = m.SentAt,
                    IsRead = m.IsRead,
                    SenderName = m.SenderId.HasValue ? (m.Sender?.Name ?? "[Usuario eliminado]") : "[Usuario eliminado]",
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
        public int? SenderId { get; set; } // ✅ Nullable para permitir anonimización completa
        public string? Content { get; set; } // ✅ Nullable para permitir anonimización
        public DateTime SentAt { get; set; }
        public bool IsRead { get; set; }
        public string? SenderName { get; set; } // ✅ Nullable para manejar usuarios eliminados
        public string? LocationLatitude { get; set; }
        public string? LocationLongitude { get; set; }
        public List<string> AttachmentUrls { get; set; } = new List<string>();
    }

    public class UploadDeliverableDto
    {
        public List<IFormFile>? Files { get; set; } // ✅ Nullable para permitir validación
    }

    public class DeliverableResponseDto
    {
        public int SearchHireId { get; set; }
        public List<string> DeliverableUrls { get; set; } = new List<string>();
        public DateTime CreatedAt { get; set; }
    }
}