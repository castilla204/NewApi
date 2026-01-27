using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using Microsoft.AspNetCore.Authorization;
using System.Threading.Tasks;
using System.Linq;
using System;
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
    // ✅ 2026: SignalR reemplazado por Supabase Realtime para chat en tiempo real

    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ChatController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ISupabaseRealtimeService _realtimeService; // ✅ Supabase Realtime en lugar de SignalR
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
            ISupabaseRealtimeService realtimeService, // ✅ Supabase Realtime en lugar de IHubContext
            StorageClient storageClient,
            IConfiguration configuration,
            IAuthorizationServices authService,
            ILoggingService loggingService,
            ISignedUrlService signedUrlService)
        {
            _context = context;
            _realtimeService = realtimeService;
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
                // ✅ CORRECCIÓN: SearchHire puede ser nullable ahora
                var conversation = await _context.Conversations
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Sender)
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Attachments)
                    .Include(c => c.Client)
                    .Include(c => c.Expert)
                    .Include(c => c.SearchHire)
                    .FirstOrDefaultAsync(c => c.SearchHire != null && 
                                             c.SearchHire.SearchId == searchId &&
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

                    // ✅ VALIDACIÓN: Asegurar que cliente y experto sean diferentes (si ambos existen)
                    if (searchHire.ClientId.HasValue && searchHire.ExpertId.HasValue && 
                        searchHire.ClientId.Value == searchHire.ExpertId.Value)
                    {
                        return BadRequest(new { message = "Client and expert cannot be the same user" });
                    }

                    // ✅ CORRECCIÓN: Permitir crear conversación incluso si ExpertId es NULL (experto borró cuenta)
                    // La conversación debe preservarse para que el cliente pueda ver el historial
                    // ✅ GARANTÍA: Solo 2 participantes (cliente y experto, o solo cliente si experto borró cuenta)
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
        /// ✅ NUEVO: Obtener todas las conversaciones previas a contratar del experto
        /// Permite al experto ver quién le ha hablado antes de contratar
        /// </summary>
        [HttpGet("pre-hire-conversations")]
        public async Task<ActionResult<List<PreHireConversationSummaryDto>>> GetPreHireConversations()
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                // Buscar todas las conversaciones previas donde el usuario es el experto
                var conversations = await _context.Conversations
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Sender)
                    .Include(c => c.Client)
                    .Include(c => c.SearchService)
                        .ThenInclude(ss => ss.Images)
                    .Include(c => c.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                    .Where(c => c.ExpertId.HasValue && 
                               c.ExpertId.Value == userId &&
                               c.SearchHireId == null && // ✅ Solo conversaciones previas
                               c.SearchServiceId != null &&
                               c.IsActive == true)
                    .OrderByDescending(c => c.UpdatedAt)
                    .ToListAsync();

                var conversationSummaries = conversations.Select(c => 
                {
                    // Obtener último mensaje (ordenado por fecha descendente)
                    var lastMessage = c.Messages
                        .OrderByDescending(m => m.SentAt)
                        .FirstOrDefault();
                    
                    // Contar mensajes no leídos (del cliente, no del experto)
                    var unreadCount = c.Messages
                        .Count(m => !m.IsRead && m.SenderId != userId);

                    return new PreHireConversationSummaryDto
                    {
                        ConversationId = c.Id,
                        SearchServiceId = c.SearchServiceId ?? 0,
                        ServiceName = c.SearchService?.ServiceType?.Name ?? "Servicio",
                        ServicePrice = c.SearchService?.Price ?? 0,
                        ServiceImageUrl = c.SearchService?.Images?.FirstOrDefault()?.ImageUrl,
                        ClientId = c.ClientId,
                        ClientName = c.Client?.Name ?? "[Usuario eliminado]",
                        ClientProfilePictureUrl = c.Client?.ExpertProfile?.ProfilePictureUrl,
                        LastMessage = lastMessage != null ? new MessageSummaryDto
                        {
                            Id = lastMessage.Id,
                            Content = lastMessage.Content ?? "",
                            SentAt = lastMessage.SentAt,
                            SenderId = lastMessage.SenderId,
                            SenderName = lastMessage.Sender?.Name ?? "[Usuario eliminado]",
                            IsRead = lastMessage.IsRead
                        } : null,
                        UnreadCount = unreadCount,
                        CreatedAt = c.CreatedAt,
                        UpdatedAt = c.UpdatedAt
                    };
                }).ToList();

                return Ok(conversationSummaries);
            }
            catch (Exception ex)
            {
                await _loggingService.LogErrorAsync(
                    message: "Error getting pre-hire conversations",
                    details: $"Error retrieving pre-hire conversations for expert: {ex.Message}",
                    source: "ChatController.GetPreHireConversations",
                    relatedEntityType: "Conversation",
                    additionalData: new { 
                        Exception = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                
                return StatusCode(500, new { message = "An error occurred while retrieving pre-hire conversations" });
            }
        }

        /// <summary>
        /// ✅ NUEVO: Obtener todas las conversaciones del cliente (pre y post contratación)
        /// Devuelve un listado unificado similar a WhatsApp/Wallapop
        /// </summary>
        [HttpGet("my-conversations")]
        public async Task<ActionResult<List<ClientConversationSummaryDto>>> GetMyConversations()
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                // Buscar todas las conversaciones donde el usuario es el cliente
                var conversations = await _context.Conversations
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Sender)
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Attachments)
                    .Include(c => c.Expert)
                        .ThenInclude(e => e.ExpertProfile)
                    .Include(c => c.SearchService)
                        .ThenInclude(ss => ss.Images)
                    .Include(c => c.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                    .Include(c => c.SearchService)
                        .ThenInclude(ss => ss.ExpertProfile)
                            .ThenInclude(ep => ep.User)
                    .Include(c => c.SearchHire)
                        .ThenInclude(sh => sh.SearchService)
                            .ThenInclude(ss => ss.Images)
                    .Include(c => c.SearchHire)
                        .ThenInclude(sh => sh.SearchService)
                            .ThenInclude(ss => ss.ServiceType)
                    .Include(c => c.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                            .ThenInclude(e => e.ExpertProfile)
                    .Include(c => c.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(c => c.SearchHire)
                        .ThenInclude(sh => sh.Search)
                    .Where(c => c.ClientId.HasValue && 
                               c.ClientId.Value == userId &&
                               c.IsActive == true)
                    .OrderByDescending(c => c.UpdatedAt)
                    .ToListAsync();

                var conversationSummaries = conversations.Select(c =>
                {
                    // Obtener último mensaje (ordenado por fecha descendente)
                    var lastMessage = c.Messages
                        .OrderByDescending(m => m.SentAt)
                        .FirstOrDefault();

                    // Contar mensajes no leídos (donde el remitente no es el usuario actual)
                    var unreadCount = c.Messages
                        .Count(m => !m.IsRead && m.SenderId.HasValue && m.SenderId.Value != userId);

                    // Determinar tipo de conversación y obtener información correspondiente
                    var isPreHire = c.SearchHireId == null && c.SearchServiceId != null;
                    var isPostHire = c.SearchHireId != null;

                    var summary = new ClientConversationSummaryDto
                    {
                        ConversationId = c.Id,
                        ConversationType = isPreHire ? "pre-hire" : "post-hire",
                        CreatedAt = c.CreatedAt,
                        UpdatedAt = c.UpdatedAt,
                        UnreadCount = unreadCount,
                        LastMessage = lastMessage != null ? new MessageSummaryDto
                        {
                            Id = lastMessage.Id,
                            Content = lastMessage.Content ?? "[Mensaje eliminado]",
                            SentAt = lastMessage.SentAt,
                            SenderId = lastMessage.SenderId,
                            SenderName = lastMessage.SenderId.HasValue 
                                ? (lastMessage.Sender?.Name ?? "[Usuario eliminado]") 
                                : "[Usuario eliminado]",
                            IsRead = lastMessage.IsRead
                        } : null
                    };

                    // Información del experto (común para ambos tipos)
                    if (c.Expert != null)
                    {
                        summary.ExpertId = c.Expert.Id;
                        summary.ExpertName = c.Expert.Name;
                        summary.ExpertProfilePictureUrl = c.Expert.ExpertProfile?.ProfilePictureUrl;
                    }

                    // Información para conversaciones PRE-CONTRATACIÓN
                    if (isPreHire && c.SearchService != null)
                    {
                        summary.SearchServiceId = c.SearchServiceId;
                        summary.ServiceName = c.SearchService.ServiceType?.Name ?? "Servicio";
                        summary.ServicePrice = c.SearchService.Price;
                        summary.ServiceImageUrl = c.SearchService.Images
                            .OrderBy(img => img.Id)
                            .FirstOrDefault()?.ImageUrl;
                        
                        // Información adicional del experto desde el servicio
                        if (c.SearchService.ExpertProfile?.User != null)
                        {
                            summary.ExpertName = c.SearchService.ExpertProfile.User.Name;
                            summary.ExpertProfilePictureUrl = c.SearchService.ExpertProfile.ProfilePictureUrl;
                        }
                    }

                    // Información para conversaciones POST-CONTRATACIÓN
                    if (isPostHire && c.SearchHire != null)
                    {
                        summary.SearchHireId = c.SearchHireId;
                        summary.HireStatus = c.SearchHire.Status?.StatusValue ?? "Unknown";
                        summary.HireStatusTranslated = c.SearchHire.Status?.DisplayName ?? "Desconocido";
                        summary.HireCreatedAt = c.SearchHire.CreatedAt;
                        summary.HireAmount = c.SearchHire.Amount;
                        summary.HireBaseAmount = c.SearchHire.BaseAmount;
                        summary.HireTaxAmount = c.SearchHire.TaxAmount;

                        // Información del servicio desde la contratación
                        if (c.SearchHire.SearchService != null)
                        {
                            summary.SearchServiceId = c.SearchHire.SearchServiceId;
                            summary.ServiceName = c.SearchHire.SearchService.ServiceType?.Name ?? "Servicio";
                            summary.ServicePrice = c.SearchHire.SearchService.Price;
                            summary.ServiceImageUrl = c.SearchHire.SearchService.Images
                                .OrderBy(img => img.Id)
                                .FirstOrDefault()?.ImageUrl;
                        }

                        // Información del experto desde la contratación
                        if (c.SearchHire.Expert != null)
                        {
                            summary.ExpertId = c.SearchHire.Expert.Id;
                            summary.ExpertName = c.SearchHire.Expert.Name;
                            summary.ExpertProfilePictureUrl = c.SearchHire.Expert.ExpertProfile?.ProfilePictureUrl;
                        }

                        // Información de la búsqueda si existe
                        if (c.SearchHire.Search != null)
                        {
                            summary.SearchTitle = c.SearchHire.Search.Title;
                            summary.SearchDescription = c.SearchHire.Search.Description;
                        }
                    }

                    return summary;
                }).ToList();

                return Ok(conversationSummaries);
            }
            catch (Exception ex)
            {
                await _loggingService.LogErrorAsync(
                    message: "Error getting client conversations",
                    details: $"Error retrieving conversations for client: {ex.Message}",
                    userId: int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : null,
                    source: "ChatController.GetMyConversations",
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
        /// ✅ NUEVO: Obtener o crear conversación previa a contratar por SearchServiceId
        /// Permite chatear antes de contratar el servicio
        /// </summary>
        [HttpGet("conversation-by-service")]
        public async Task<ActionResult<ConversationDto>> GetConversationBySearchServiceId([FromQuery] int searchServiceId)
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                // Buscar el servicio
                var searchService = await _context.SearchServices
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                    .FirstOrDefaultAsync(ss => ss.Id == searchServiceId);

                if (searchService == null)
                {
                    return NotFound(new { message = "Search service not found" });
                }

                // ✅ CORRECCIÓN: Permitir que el experto también acceda a la conversación
                // El experto puede ver y responder a los mensajes de los clientes que le escriben
                // No restringimos el acceso, solo evitamos que el experto cree conversaciones consigo mismo
                var expertUserId = searchService.ExpertProfile?.User?.Id;
                var isExpert = expertUserId.HasValue && expertUserId.Value == userId;
                var isAdmin = _authService.IsAdmin(User);

                // ✅ CORRECCIÓN: Buscar conversación existente por SearchServiceId
                // Priorizar conversación del cliente si el usuario es cliente
                // Si es experto, puede ver todas sus conversaciones pero necesitamos un ClientId específico
                var conversation = await _context.Conversations
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Sender)
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Attachments)
                    .Include(c => c.Client)
                    .Include(c => c.Expert)
                    .Where(c => c.SearchServiceId == searchServiceId &&
                               c.IsActive == true &&
                               ((c.ClientId.HasValue && c.ClientId.Value == userId) || 
                                (c.ExpertId.HasValue && c.ExpertId.Value == userId) || 
                                _authService.IsAdmin(User)))
                    .OrderByDescending(c => c.ClientId == userId) // ✅ Priorizar conversación del cliente si es cliente
                    .ThenByDescending(c => c.UpdatedAt) // Luego por más reciente
                    .FirstOrDefaultAsync();

                if (conversation == null)
                {
                    // ✅ CORRECCIÓN: Cliente y Admin pueden crear conversaciones previas
                    // Si el usuario es el experto (y no es admin) y no existe conversación, retornar NotFound
                    if (isExpert && !isAdmin)
                    {
                        return NotFound(new { message = "No conversation found for this service. Only clients and admins can start pre-hire conversations." });
                    }

                    // ✅ VALIDACIÓN: Asegurar que cliente y experto sean diferentes
                    if (userId == expertUserId)
                    {
                        return BadRequest(new { message = "Client and expert cannot be the same user" });
                    }

                    // Crear nueva conversación previa (si es cliente o admin)
                    // ✅ GARANTÍA: Solo 2 participantes (cliente y experto)
                    // ✅ PERMISO: Admin puede crear para pruebas
                    conversation = new Conversation
                    {
                        SearchServiceId = searchServiceId,
                        SearchHireId = null, // ✅ No hay SearchHire aún
                        ClientId = userId, // El usuario actual es el cliente (o admin haciendo de cliente)
                        ExpertId = expertUserId, // El experto del servicio
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        Messages = new List<Message>()
                    };

                    _context.Conversations.Add(conversation);
                    await _context.SaveChangesAsync();
                    
                    await _loggingService.LogInfoAsync(
                        message: "New pre-hire conversation created",
                        details: $"New pre-hire conversation created for SearchServiceId {searchServiceId}. ConversationId: {conversation.Id}, ClientId: {conversation.ClientId}, ExpertId: {conversation.ExpertId}. Created by: {(isAdmin ? "Admin" : "Client")}",
                        userId: userId,
                        source: "ChatController.GetConversationBySearchServiceId",
                        relatedEntityType: "Conversation",
                        relatedEntityId: conversation.Id,
                        additionalData: new { 
                            Action = "CreatePreHireConversation",
                            SearchServiceId = searchServiceId,
                            ConversationId = conversation.Id,
                            ClientId = conversation.ClientId,
                            ExpertId = conversation.ExpertId,
                            CreatedByAdmin = isAdmin
                        }
                    );
                }

                // ✅ VALIDACIÓN ADICIONAL: Verificar que la conversación es correcta
                // Si el usuario es cliente, asegurar que es su conversación
                if (!isExpert && !_authService.IsAdmin(User))
                {
                    if (!conversation.ClientId.HasValue || conversation.ClientId.Value != userId)
                    {
                        await _loggingService.LogErrorAsync(
                            message: "Security issue: Client accessing wrong conversation",
                            details: $"Client {userId} attempted to access conversation {conversation.Id} with ClientId {conversation.ClientId} for SearchServiceId {searchServiceId}",
                            userId: userId,
                            source: "ChatController.GetConversationBySearchServiceId",
                            relatedEntityType: "Conversation",
                            relatedEntityId: conversation.Id,
                            additionalData: new { 
                                RequestedSearchServiceId = searchServiceId,
                                ConversationId = conversation.Id,
                                ConversationClientId = conversation.ClientId,
                                RequestingUserId = userId
                            }
                        );
                        return Unauthorized(new { message = "You are not authorized to access this conversation" });
                    }
                }
                
                // ✅ LOGGING: Registrar cuando admin accede a conversación pre-contratación
                if (_authService.IsAdmin(User) && 
                    conversation.ClientId.HasValue && conversation.ClientId.Value != userId && 
                    conversation.ExpertId.HasValue && conversation.ExpertId.Value != userId)
                {
                    await _loggingService.LogWarningAsync(
                        message: "Admin accessed pre-hire conversation",
                        details: $"Admin {userId} accessed pre-hire conversation {conversation.Id} for SearchServiceId {searchServiceId}. ClientId: {conversation.ClientId}, ExpertId: {conversation.ExpertId}",
                        userId: userId,
                        source: "ChatController.GetConversationBySearchServiceId",
                        relatedEntityType: "Conversation",
                        relatedEntityId: conversation.Id,
                        additionalData: new { 
                            SearchServiceId = searchServiceId,
                            ConversationId = conversation.Id,
                            ConversationClientId = conversation.ClientId,
                            ConversationExpertId = conversation.ExpertId,
                            AdminUserId = userId,
                            Action = "AdminAccessPreHireConversation"
                        }
                    );
                }

                var conversationDto = ConversationDto.FromConversation(conversation);
                PopulateSignedAttachmentUrls(conversation, conversationDto);
                
                // ✅ LOG: Registrar acceso a conversación para debugging
                await _loggingService.LogInfoAsync(
                    message: "Conversation accessed",
                    details: $"User {userId} accessed conversation {conversation.Id} for SearchServiceId {searchServiceId}. ClientId: {conversation.ClientId}, ExpertId: {conversation.ExpertId}, MessageCount: {conversation.Messages?.Count ?? 0}",
                    userId: userId,
                    source: "ChatController.GetConversationBySearchServiceId",
                    relatedEntityType: "Conversation",
                    relatedEntityId: conversation.Id,
                    additionalData: new { 
                        SearchServiceId = searchServiceId,
                        ConversationId = conversation.Id,
                        ClientId = conversation.ClientId,
                        ExpertId = conversation.ExpertId,
                        MessageCount = conversation.Messages?.Count ?? 0,
                        IsExpert = isExpert
                    }
                );
                
                return Ok(conversationDto);
            }
            catch (Exception ex)
            {
                await _loggingService.LogErrorAsync(
                    message: "Error getting conversation by SearchServiceId",
                    details: $"Error retrieving conversation for SearchServiceId {searchServiceId}: {ex.Message}",
                    source: "ChatController.GetConversationBySearchServiceId",
                    relatedEntityType: "Conversation",
                    additionalData: new { 
                        SearchServiceId = searchServiceId,
                        Exception = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                
                return StatusCode(500, new { message = "Failed to retrieve conversation", detail = ex.Message });
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

                    // ✅ VALIDACIÓN: Asegurar que cliente y experto sean diferentes (si ambos existen)
                    if (searchHire.ClientId.HasValue && searchHire.ExpertId.HasValue && 
                        searchHire.ClientId.Value == searchHire.ExpertId.Value)
                    {
                        return BadRequest(new { message = "Client and expert cannot be the same user" });
                    }

                    // ✅ CORRECCIÓN: Permitir crear conversación incluso si ExpertId es NULL (experto borró cuenta)
                    // La conversación debe preservarse para que el cliente pueda ver el historial
                    // ✅ GARANTÍA: Solo 2 participantes (cliente y experto, o solo cliente si experto borró cuenta)
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

                // ✅ VALIDACIÓN CRÍTICA: Asegurar que solo cliente y experto puedan enviar mensajes
                // Prevenir que más de 2 personas participen en la conversación
                var isClient = conversation.ClientId.HasValue && conversation.ClientId.Value == userId;
                var isExpert = conversation.ExpertId.HasValue && conversation.ExpertId.Value == userId;
                
                if (!isClient && !isExpert)
                {
                    // ⚠️ CRÍTICO: Usuario no autorizado intentando enviar mensaje
                    await _loggingService.LogErrorAsync(
                        message: "CRITICAL: Unauthorized user attempting to send message",
                        details: $"User {userId} attempted to send a message to conversation {conversation.Id} where they are not the client ({conversation.ClientId}) or expert ({conversation.ExpertId}). This violates the 2-person conversation rule.",
                        userId: userId,
                        source: "ChatController.SendMessage",
                        relatedEntityType: "Conversation",
                        relatedEntityId: conversation.Id,
                        additionalData: new { 
                            ConversationId = conversation.Id,
                            ConversationClientId = conversation.ClientId,
                            ConversationExpertId = conversation.ExpertId,
                            AttemptingUserId = userId,
                            IsAdmin = isAdmin
                        }
                    );
                    return Unauthorized(new { message = "Only the client and expert can send messages in this conversation" });
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
                    // ✅ LOG: Inicio del proceso de subida de archivos
                    await _loggingService.LogInfoAsync(
                        message: "Starting file upload process",
                        details: $"Starting upload of {dto.Attachments.Count} file(s) for conversation {dto.ConversationId}",
                        userId: userId,
                        source: "ChatController.SendMessage",
                        relatedEntityType: "Message",
                        relatedEntityId: dto.ConversationId,
                        additionalData: new { 
                            ConversationId = dto.ConversationId, 
                            FileCount = dto.Attachments.Count,
                            FileNames = dto.Attachments.Select(f => f.FileName).ToList()
                        },
                        notifyUser: false
                    );

                    // ✅ CORRECCIÓN: Validar que StorageClient esté disponible
                    if (_storageClient == null)
                    {
                        await _loggingService.LogErrorAsync(
                            message: "StorageClient not available for file upload",
                            details: "Google Cloud Storage client is not configured. Cannot upload attachments.",
                            userId: userId,
                            source: "ChatController.SendMessage",
                            relatedEntityType: "Message",
                            relatedEntityId: dto.ConversationId,
                            additionalData: new { ConversationId = dto.ConversationId, FileCount = dto.Attachments.Count }
                        );
                        return StatusCode(500, new { message = "File upload service is not available. Please contact support." });
                    }

                    var bucketName = _configuration["GoogleCloud:BucketName"];
                    if (string.IsNullOrEmpty(bucketName))
                    {
                        await _loggingService.LogErrorAsync(
                            message: "Bucket name not configured",
                            details: "GoogleCloud:BucketName is not configured in app settings.",
                            userId: userId,
                            source: "ChatController.SendMessage",
                            relatedEntityType: "Message",
                            relatedEntityId: dto.ConversationId,
                            additionalData: new { ConversationId = dto.ConversationId }
                        );
                        return StatusCode(500, new { message = "File upload configuration error. Please contact support." });
                    }

                    // ✅ LOG: Configuración validada
                    await _loggingService.LogInfoAsync(
                        message: "File upload configuration validated",
                        details: $"StorageClient available, bucket name: {bucketName}",
                        userId: userId,
                        source: "ChatController.SendMessage",
                        relatedEntityType: "Message",
                        relatedEntityId: dto.ConversationId,
                        additionalData: new { BucketName = bucketName },
                        notifyUser: false
                    );

                    foreach (var file in dto.Attachments)
                    {
                        // ✅ LOG: Inicio de subida de archivo individual
                        await _loggingService.LogInfoAsync(
                            message: "Starting individual file upload",
                            details: $"Starting upload of file: {file.FileName}",
                            userId: userId,
                            source: "ChatController.SendMessage",
                            relatedEntityType: "Message",
                            relatedEntityId: dto.ConversationId,
                            additionalData: new { 
                                FileName = file.FileName,
                                FileSize = file.Length,
                                ContentType = file.ContentType,
                                MessageId = message.Id
                            },
                            notifyUser: false
                        );

                        try
                        {
                            var uploadResult = await ValidateAndUploadAttachmentAsync(file, bucketName, dto.ConversationId, userId);
                            
                            // ✅ LOG: Archivo validado y subido exitosamente
                            await _loggingService.LogInfoAsync(
                                message: "File uploaded successfully",
                                details: $"File {file.FileName} uploaded successfully to {uploadResult.ObjectName}",
                                userId: userId,
                                source: "ChatController.SendMessage",
                                relatedEntityType: "Message",
                                relatedEntityId: message.Id,
                                additionalData: new { 
                                    FileName = file.FileName,
                                    ObjectName = uploadResult.ObjectName,
                                    Url = uploadResult.Url,
                                    Type = uploadResult.Type
                                },
                                notifyUser: false
                            );

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
                            await _loggingService.LogWarningAsync(
                                message: "Invalid file upload attempt",
                                details: $"Invalid file upload: {ex.Message}",
                                userId: userId,
                                source: "ChatController.SendMessage",
                                relatedEntityType: "Message",
                                relatedEntityId: dto.ConversationId,
                                additionalData: new { 
                                    FileName = file.FileName, 
                                    FileSize = file.Length,
                                    Exception = ex.Message
                                }
                            );
                            return BadRequest(new { message = ex.Message });
                        }
                        catch (Exception ex)
                        {
                            await _loggingService.LogErrorAsync(
                                message: "Error uploading file for message",
                                details: $"Failed to upload file {file.FileName}: {ex.Message}",
                                userId: userId,
                                source: "ChatController.SendMessage",
                                relatedEntityType: "Message",
                                relatedEntityId: dto.ConversationId,
                                additionalData: new
                                {
                                    FileName = file.FileName,
                                    FileSize = file.Length,
                                    ContentType = file.ContentType,
                                    Exception = ex.Message,
                                    StackTrace = ex.StackTrace
                                }
                            );
                            return StatusCode(500, new { message = $"Failed to upload file {file.FileName}. Please try again or contact support." });
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

                // ✅ 2026: Notificar nuevo mensaje via Supabase Realtime (reemplaza SignalR)
                // El mensaje ya está en la BD, Postgres Changes notificará automáticamente
                // Este broadcast es adicional para typing indicators y notificaciones inmediatas
                try
                {
                    await _realtimeService.NotifyNewMessageAsync(dto.ConversationId, messageDto);
                    
                    await _loggingService.LogInfoAsync(
                        message: "Message notification sent via Supabase Realtime",
                        details: $"Message {messageDto.Id} notification sent to conversation {dto.ConversationId}",
                        userId: userId,
                        source: "ChatController.SendMessage",
                        relatedEntityType: "Message",
                        relatedEntityId: messageDto.Id,
                        additionalData: new { 
                            MessageId = messageDto.Id,
                            ConversationId = dto.ConversationId,
                            HasAttachments = messageDto.AttachmentUrls.Any()
                        },
                        notifyUser: false
                    );
                }
                catch (Exception realtimeEx)
                {
                    // El mensaje ya está guardado en la BD, Postgres Changes lo notificará
                    // Este error solo afecta al broadcast adicional
                    await _loggingService.LogWarningAsync(
                        message: "Supabase Realtime broadcast warning",
                        details: $"Message {messageDto.Id} saved but optional broadcast failed: {realtimeEx.Message}",
                        userId: userId,
                        source: "ChatController.SendMessage",
                        relatedEntityType: "Message",
                        relatedEntityId: messageDto.Id,
                        additionalData: new { 
                            MessageId = messageDto.Id,
                            ConversationId = dto.ConversationId,
                            Exception = realtimeEx.Message
                        }
                    );
                    // No lanzar excepción - el mensaje ya está guardado y Postgres Changes lo notificará
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

                // ✅ 2026: Notificar via Supabase Realtime (opcional, Postgres Changes también lo notifica)
                try
                {
                    await _realtimeService.BroadcastToChannelAsync(
                        $"conversation:{message.ConversationId}", 
                        "message_read", 
                        new { messageId, conversationId = message.ConversationId });
                }
                catch (Exception)
                {
                    // Silently ignore - Postgres Changes will notify the change
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
                            // ✅ CORRECCIÓN: No usar PredefinedAcl si el bucket tiene uniform bucket-level access habilitado
                            // El acceso se controla mediante IAM policies del bucket, no ACLs por objeto
                            await _storageClient.UploadObjectAsync(
                                bucket: bucketName,
                                objectName: objectName,
                                contentType: contentType,
                                source: inputStream);
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

                // ✅ 2026: Notificar via Supabase Realtime
                try
                {
                    var conversation = await _context.Conversations
                        .FirstOrDefaultAsync(c => c.SearchHireId == searchHireId);
                    if (conversation != null)
                    {
                        await _realtimeService.BroadcastToChannelAsync(
                            $"conversation:{conversation.Id}", 
                            "deliverable_uploaded", 
                            response);
                    }
                }
                catch (Exception)
                {
                    // Silently ignore - deliverable is already saved
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

        /// <summary>
        /// ✅ 2026: Endpoint para notificar que el usuario está escribiendo
        /// Reemplaza el método UserTyping de SignalR
        /// </summary>
        [HttpPost("typing")]
        public async Task<ActionResult> NotifyTyping([FromBody] TypingNotificationDto dto)
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                var conversation = await _context.Conversations
                    .FirstOrDefaultAsync(c => c.Id == dto.ConversationId);

                if (conversation == null)
                {
                    return NotFound(new { message = "Conversation not found" });
                }

                var isAdmin = _authService.IsAdmin(User);
                if (!UserBelongsToConversation(conversation, userId, isAdmin))
                {
                    return Unauthorized(new { message = "You are not authorized for this conversation" });
                }

                await _realtimeService.NotifyUserTypingAsync(dto.ConversationId, userId, dto.IsTyping);
                
                return Ok();
            }
            catch (Exception ex)
            {
                await _loggingService.LogErrorAsync(
                    message: "Error sending typing notification",
                    details: ex.Message,
                    source: "ChatController.NotifyTyping"
                );
                return StatusCode(500, new { message = "Failed to send typing notification" });
            }
        }

        private bool UserBelongsToConversation(Conversation conversation, int userId, bool isAdmin)
        {
            if (conversation == null)
            {
                return false;
            }

            // ✅ CORRECCIÓN: Solo cliente y experto pueden enviar mensajes
            // Los admins pueden VER conversaciones pero NO enviar mensajes
            // Si necesitas que admins puedan intervenir, debe ser explícito y con logging
            
            if (conversation.ClientId.HasValue && conversation.ClientId.Value == userId)
            {
                return true;
            }

            if (conversation.ExpertId.HasValue && conversation.ExpertId.Value == userId)
            {
                return true;
            }

            // ⚠️ Si es admin, registrar warning pero NO permitir enviar mensajes
            // Los admins pueden ver conversaciones para soporte, pero no deben enviar mensajes
            if (isAdmin)
            {
                // Logging para detectar intentos de admins enviando mensajes
                _loggingService.LogWarningAsync(
                    message: "Admin attempted to send message to conversation",
                    details: $"Admin {userId} attempted to send a message to conversation {conversation.Id} where they are not the client or expert. ClientId: {conversation.ClientId}, ExpertId: {conversation.ExpertId}. This action was blocked for security.",
                    userId: userId,
                    source: "ChatController.UserBelongsToConversation",
                    relatedEntityType: "Conversation",
                    relatedEntityId: conversation.Id,
                    additionalData: new { 
                        ConversationId = conversation.Id,
                        ClientId = conversation.ClientId,
                        ExpertId = conversation.ExpertId,
                        AdminUserId = userId,
                        Action = "SendMessageBlocked"
                    }
                ).Wait(); // Fire and forget logging
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
            // ✅ LOG: Inicio de validación
            await _loggingService.LogInfoAsync(
                message: "Starting file validation",
                details: $"Validating file: {file?.FileName ?? "null"}",
                userId: userId,
                source: "ChatController.ValidateAndUploadAttachmentAsync",
                relatedEntityType: "Message",
                relatedEntityId: conversationId,
                additionalData: new { 
                    FileName = file?.FileName,
                    FileSize = file?.Length ?? 0,
                    ContentType = file?.ContentType
                },
                notifyUser: false
            );

            // ✅ CORRECCIÓN: Validaciones mejoradas
            if (file == null || file.Length == 0)
            {
                await _loggingService.LogErrorAsync(
                    message: "File validation failed: empty file",
                    details: "File is null or has zero length",
                    userId: userId,
                    source: "ChatController.ValidateAndUploadAttachmentAsync",
                    relatedEntityType: "Message",
                    relatedEntityId: conversationId,
                    additionalData: new { FileName = file?.FileName }
                );
                throw new InvalidOperationException("Attachment is empty");
            }

            if (file.Length > MaxAttachmentSizeBytes)
            {
                await _loggingService.LogWarningAsync(
                    message: "File validation failed: file too large",
                    details: $"File {file.FileName} size {file.Length} exceeds limit {MaxAttachmentSizeBytes}",
                    userId: userId,
                    source: "ChatController.ValidateAndUploadAttachmentAsync",
                    relatedEntityType: "Message",
                    relatedEntityId: conversationId,
                    additionalData: new { 
                        FileName = file.FileName,
                        FileSize = file.Length,
                        MaxSize = MaxAttachmentSizeBytes
                    }
                );
                throw new InvalidOperationException($"File {file.FileName} exceeds 10MB limit");
            }

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var providedContentType = (file.ContentType ?? string.Empty).ToLowerInvariant();
            
            // ✅ LOG: Información del archivo
            await _loggingService.LogInfoAsync(
                message: "File information extracted",
                details: $"File extension: {extension}, ContentType: {providedContentType}",
                userId: userId,
                source: "ChatController.ValidateAndUploadAttachmentAsync",
                relatedEntityType: "Message",
                relatedEntityId: conversationId,
                additionalData: new { 
                    FileName = file.FileName,
                    Extension = extension,
                    ProvidedContentType = providedContentType
                },
                notifyUser: false
            );

            // ✅ CORRECCIÓN: Validación más flexible - aceptar por extensión principalmente
            // Algunos navegadores envían "application/octet-stream" o ContentType incorrecto
            // Si la extensión es válida, aceptamos el archivo y inferimos el ContentType correcto
            var isImageByExtension = AllowedImageExtensions.Contains(extension);
            var isVideoByExtension = AllowedVideoExtensions.Contains(extension);

            if (!isImageByExtension && !isVideoByExtension)
            {
                throw new InvalidOperationException($"Unsupported file type: {file.FileName} (extension: {extension}). Allowed types: {string.Join(", ", AllowedImageExtensions)} for images, {string.Join(", ", AllowedVideoExtensions)} for videos");
            }

            // Si la extensión es válida pero el ContentType no coincide, solo logueamos una advertencia
            // pero aceptamos el archivo (confiamos en la extensión)
            var isImageByContentType = AllowedImageContentTypes.Contains(providedContentType);
            var isVideoByContentType = AllowedVideoContentTypes.Contains(providedContentType);
            
            if (isImageByExtension && !isImageByContentType && !string.IsNullOrEmpty(providedContentType) && providedContentType != "application/octet-stream")
            {
                // Loguear advertencia pero continuar
                await _loggingService.LogWarningAsync(
                    message: "ContentType mismatch for image file",
                    details: $"File {file.FileName} has valid extension {extension} but ContentType is {providedContentType}. Accepting based on extension.",
                    userId: userId,
                    source: "ChatController.ValidateAndUploadAttachmentAsync",
                    relatedEntityType: "Message",
                    relatedEntityId: conversationId,
                    additionalData: new { FileName = file.FileName, Extension = extension, ContentType = providedContentType }
                );
            }
            
            if (isVideoByExtension && !isVideoByContentType && !string.IsNullOrEmpty(providedContentType) && providedContentType != "application/octet-stream")
            {
                // Loguear advertencia pero continuar
                await _loggingService.LogWarningAsync(
                    message: "ContentType mismatch for video file",
                    details: $"File {file.FileName} has valid extension {extension} but ContentType is {providedContentType}. Accepting based on extension.",
                    userId: userId,
                    source: "ChatController.ValidateAndUploadAttachmentAsync",
                    relatedEntityType: "Message",
                    relatedEntityId: conversationId,
                    additionalData: new { FileName = file.FileName, Extension = extension, ContentType = providedContentType }
                );
            }

            var isImage = isImageByExtension;
            var isVideo = isVideoByExtension;

            // ✅ CORRECCIÓN: Inferir ContentType correcto basándose en la extensión si no está presente o es incorrecto
            string contentType;
            if (isImage)
            {
                if (extension == ".png")
                    contentType = "image/png";
                else if (extension == ".jpg" || extension == ".jpeg")
                    contentType = "image/jpeg";
                else
                    contentType = providedContentType; // Fallback al proporcionado
            }
            else if (isVideo)
            {
                contentType = "video/mp4";
            }
            else
            {
                contentType = providedContentType; // No debería llegar aquí, pero por seguridad
            }

            // ✅ CORRECCIÓN: Validar que StorageClient esté disponible
            if (_storageClient == null)
            {
                throw new InvalidOperationException("File upload service is not available. Please contact support.");
            }

            var uniqueFileName = $"{Guid.NewGuid()}{extension}";
            var objectName = $"messages/{uniqueFileName}";

            // ✅ LOG: Preparando subida a Google Cloud Storage
            await _loggingService.LogInfoAsync(
                message: "Preparing Google Cloud Storage upload",
                details: $"Uploading to bucket: {bucketName}, object: {objectName}",
                userId: userId,
                source: "ChatController.ValidateAndUploadAttachmentAsync",
                relatedEntityType: "Message",
                relatedEntityId: conversationId,
                additionalData: new { 
                    BucketName = bucketName,
                    ObjectName = objectName,
                    ContentType = contentType,
                    FileSize = file.Length
                },
                notifyUser: false
            );

            try
            {
                using var inputStream = file.OpenReadStream();
                
                // ✅ LOG: Iniciando upload
                await _loggingService.LogInfoAsync(
                    message: "Starting Google Cloud Storage upload",
                    details: $"Uploading file stream to {objectName}",
                    userId: userId,
                    source: "ChatController.ValidateAndUploadAttachmentAsync",
                    relatedEntityType: "Message",
                    relatedEntityId: conversationId,
                    additionalData: new { ObjectName = objectName },
                    notifyUser: false
                );

                // ✅ CORRECCIÓN: No usar PredefinedAcl si el bucket tiene uniform bucket-level access habilitado
                // El acceso se controla mediante IAM policies del bucket, no ACLs por objeto
                await _storageClient.UploadObjectAsync(
                    bucket: bucketName,
                    objectName: objectName,
                    contentType: contentType,
                    source: inputStream);
                
                // ✅ LOG: Upload completado exitosamente
                await _loggingService.LogInfoAsync(
                    message: "Google Cloud Storage upload completed",
                    details: $"File successfully uploaded to {objectName}",
                    userId: userId,
                    source: "ChatController.ValidateAndUploadAttachmentAsync",
                    relatedEntityType: "Message",
                    relatedEntityId: conversationId,
                    additionalData: new { ObjectName = objectName },
                    notifyUser: false
                );
            }
            catch (Exception ex)
            {
                // ✅ MEJORA: Logging más detallado del error
                await _loggingService.LogErrorAsync(
                    message: "Error uploading file for message",
                    details: $"Error uploading file {file.FileName} for conversation {conversationId}: {ex.Message}",
                    userId: userId,
                    source: "ChatController.ValidateAndUploadAttachmentAsync",
                    relatedEntityType: "Message",
                    relatedEntityId: conversationId,
                    additionalData: new
                    {
                        FileName = file.FileName,
                        FileSize = file.Length,
                        ContentType = contentType,
                        Extension = extension,
                        BucketName = bucketName,
                        ObjectName = objectName,
                        ConversationId = conversationId,
                        Exception = ex.Message,
                        ExceptionType = ex.GetType().Name,
                        StackTrace = ex.StackTrace
                    });

                throw new InvalidOperationException($"Failed to upload file {file.FileName}: {ex.Message}");
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
        public int? SearchHireId { get; set; } // ✅ Nullable para conversaciones previas
        public int? SearchServiceId { get; set; } // ✅ NUEVO: Para conversaciones previas a contratar
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
                SearchServiceId = conversation.SearchServiceId, // ✅ NUEVO
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

    /// <summary>
    /// ✅ 2026: DTO para notificaciones de typing (reemplaza SignalR UserTyping)
    /// </summary>
    public class TypingNotificationDto
    {
        public int ConversationId { get; set; }
        public bool IsTyping { get; set; }
    }

    /// <summary>
    /// ✅ NUEVO: DTO para resumen de conversaciones previas a contratar
    /// Usado para listar conversaciones del experto
    /// </summary>
    public class PreHireConversationSummaryDto
    {
        public int ConversationId { get; set; }
        public int SearchServiceId { get; set; }
        public string ServiceName { get; set; } = string.Empty;
        public decimal ServicePrice { get; set; }
        public string? ServiceImageUrl { get; set; }
        public int? ClientId { get; set; }
        public string ClientName { get; set; } = string.Empty;
        public string? ClientProfilePictureUrl { get; set; }
        public MessageSummaryDto? LastMessage { get; set; }
        public int UnreadCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// ✅ NUEVO: DTO para resumen de mensaje (último mensaje de la conversación)
    /// </summary>
    public class MessageSummaryDto
    {
        public int Id { get; set; }
        public string Content { get; set; } = string.Empty;
        public DateTime SentAt { get; set; }
        public int? SenderId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public bool IsRead { get; set; }
    }

    /// <summary>
    /// ✅ NUEVO: DTO para resumen de conversación del cliente (pre y post contratación)
    /// Similar a WhatsApp/Wallapop - listado unificado de todas las conversaciones
    /// </summary>
    public class ClientConversationSummaryDto
    {
        // Información básica de la conversación
        public int ConversationId { get; set; }
        public string ConversationType { get; set; } = string.Empty; // "pre-hire" o "post-hire"
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int UnreadCount { get; set; }
        public MessageSummaryDto? LastMessage { get; set; }

        // Información del experto (común para ambos tipos)
        public int? ExpertId { get; set; }
        public string ExpertName { get; set; } = string.Empty;
        public string? ExpertProfilePictureUrl { get; set; }

        // Información para PRE-CONTRATACIÓN (SearchServiceId)
        public int? SearchServiceId { get; set; }
        public string? ServiceName { get; set; }
        public decimal? ServicePrice { get; set; }
        public string? ServiceImageUrl { get; set; }

        // Información para POST-CONTRATACIÓN (SearchHireId)
        public int? SearchHireId { get; set; }
        public string? HireStatus { get; set; }
        public string? HireStatusTranslated { get; set; }
        public DateTime? HireCreatedAt { get; set; }
        public decimal? HireAmount { get; set; }
        public decimal? HireBaseAmount { get; set; }
        public decimal? HireTaxAmount { get; set; }
        public string? SearchTitle { get; set; }
        public string? SearchDescription { get; set; }
    }
}