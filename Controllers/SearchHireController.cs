using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using newApi.Services;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using System;
using System.Linq;
using Stripe;
using newApi.DataLayer.Models.DTOs;
using Hangfire;
using newApi.Common;

namespace newApi.Controllers
{
    /// <summary>
    /// Controlador para gestionar búsquedas y contrataciones de servicios.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SearchHireController : ControllerBase
    {
        private readonly SearchHireService _searchHireService;
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IAuthorizationServices _authService;
        private readonly StripeRefundService _refundService;
        private readonly ILoggingService _loggingService;
        private readonly IInvoiceService _invoiceService;
        private readonly IAppointmentService _appointmentService;
        private readonly ISignedUrlService _signedUrlService;
        private readonly INotificationService _notificationService;

        public SearchHireController(
            SearchHireService searchHireService,
            AppDbContext context,
            IConfiguration configuration,
            IAuthorizationServices authService,
            StripeRefundService refundService,
            ILoggingService loggingService,
            IInvoiceService invoiceService,
            IAppointmentService appointmentService,
            ISignedUrlService signedUrlService,
            INotificationService notificationService)
        {
            _searchHireService = searchHireService;
            _context = context;
            _configuration = configuration;
            _authService = authService;
            _refundService = refundService;
            _loggingService = loggingService;
            _invoiceService = invoiceService;
            _appointmentService = appointmentService;
            _signedUrlService = signedUrlService;
            _notificationService = notificationService;
            StripeConfiguration.ApiKey = configuration["Stripe:SecretKey"];
        }

        /// <summary>
        /// Helper method to get StatusId from StatusValue
        /// </summary>
        private async Task<int> GetStatusIdByValueAsync(string statusValue)
        {
            var systemStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.StatusType == "SearchHireStatus");
            
            if (systemStatus == null)
            {
                // ⚠️ FRENTE 8: estado no encontrado → AVISAR en vez de rebobinar a "pending" en silencio.
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: SearchHireStatus value not found - defaulting to 'pending'",
                    details: $"GetStatusIdByValueAsync could not resolve StatusValue '{statusValue}' (SearchHireStatus). Defaulting to pending (1); verify the status is seeded. This can silently misroute a hire.",
                    source: "SearchHireController.GetStatusIdByValueAsync",
                    relatedEntityType: "SearchHire");
                return 1;
            }

            return systemStatus.Id;
        }

        // POST: api/searchhire
        // RESTRINGIDO A ADMIN: este endpoint crea SearchHire sin pasar por Stripe Checkout
        // (Amount tomado directamente del Price del servicio, sin cobro). Los clientes deben
        // usar el flujo normal por /api/Subscription/HireService que sí cobra.
        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateSearchHire([FromBody] CreateSearchHireDto dto)
        {
            try
            {
                if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid or missing user ID in token" });
                }

                // 🚨 VALIDACIÓN CRÍTICA: Los expertos no pueden crear contrataciones como clientes
                // ✅ IMPORTANTE: Deben usar una cuenta distinta (no registrada como experto) para contratar
                var user = await _context.Users
                    .Include(u => u.ExpertProfile)
                    .FirstOrDefaultAsync(u => u.Id == userId);
                
                if (user == null)
                {
                    return NotFound(new { message = "User not found" });
                }

                // ✅ VALIDACIÓN: Usuario bloqueado no puede crear contrataciones
                if (user.IsBlocked)
                {
                    await _loggingService.LogWarningAsync(
                        message: "Blocked user attempted to create search hire",
                        details: $"Blocked user {user.Id} ({user.Email}) attempted to create a search hire",
                        userId: user.Id,
                        source: "SearchHireController.CreateSearchHire",
                        relatedEntityType: "User",
                        relatedEntityId: user.Id
                    );
                    return Unauthorized(new { message = "User account is blocked" });
                }

                if (user.Role == UserRole.Expert || user.ExpertProfile != null)
                {
                    return BadRequest(new { 
                        message = "Los expertos no pueden crear contrataciones. Debes usar una cuenta distinta (no registrada como experto) para contratar servicios."
                    });
                }

                var search = await _context.Searches.FindAsync(dto.SearchId);
                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                if (search.UserId != userId && !_authService.IsAdmin(User))
                {
                    return Unauthorized(new { message = "You are not authorized to create a search hire for this search" });
                }

                if (!dto.ExpertId.HasValue)
                {
                    return BadRequest(new { message = "Expert ID is required to create a search hire" });
                }

                var expert = await _context.Users
                    .Include(u => u.ExpertProfile)
                    .FirstOrDefaultAsync(u => u.Id == dto.ExpertId.Value);
                if (expert == null || expert.Role != UserRole.Expert)
                {
                    return BadRequest(new { message = "Invalid or non-expert user specified" });
                }

                if (expert.ExpertProfile == null)
                {
                    return BadRequest(new { message = "Expert has no profile configured" });
                }

                // Get the search category, if available
                var searchParameter = await _context.SearchParameters
                    .Where(sp => sp.SearchId == dto.SearchId)
                    .FirstOrDefaultAsync();
                int? categoryId = searchParameter?.Category;

                // Find a SearchService for the expert, preferably matching the search category
                SearchService searchService;
                if (categoryId.HasValue)
                {
                    searchService = await _context.SearchServices
                        .Where(ss => ss.ExpertProfileId == expert.ExpertProfile.Id && ss.CategoryId == categoryId.Value)
                        .FirstOrDefaultAsync();
                }
                else
                {
                    // Fallback to any SearchService for the expert
                    searchService = await _context.SearchServices
                        .Where(ss => ss.ExpertProfileId == expert.ExpertProfile.Id)
                        .FirstOrDefaultAsync();
                }

                if (searchService == null)
                {
                    return BadRequest(new { message = "No suitable SearchService found for the expert" });
                }

                // Obtener la disponibilidad actual del experto al momento de la contratación
                var currentAvailability = await _context.ExpertAvailabilities
                    .Where(ea => ea.ExpertId == expert.ExpertProfile.Id && ea.IsActive && ea.EffectiveTo == null)
                    .OrderByDescending(ea => ea.EffectiveFrom)
                    .FirstOrDefaultAsync();

                // ✅ INTERNACIONALIZACIÓN: Obtener timezone y country del experto al momento de crear la contratación
                // Esto crea un snapshot que protege las contrataciones activas si el experto cambia de ubicación
                var expertTimezone = expert.ExpertProfile?.Timezone ?? "UTC";
                var expertCountry = expert.ExpertProfile?.Country;

                var searchHire = new SearchHire
                {
                    SearchId = dto.SearchId,
                    ClientId = search.UserId,
                    ExpertId = dto.ExpertId.Value,
                    SearchServiceId = searchService.Id,
                    StatusId = await GetStatusIdByValueAsync("pending"),
                    Amount = searchService.Price,
                    // ✅ STRIPE TAX: Si no hay pago de Stripe (creación directa), usar Amount como BaseAmount y TaxAmount = 0
                    BaseAmount = searchService.Price, // Sin pago Stripe, el precio completo es la base
                    TaxAmount = 0, // Sin pago Stripe, no hay tax calculado
                    // 🌍 Round 21: snapshot del currency del SearchService — inmutable durante toda la vida del hire.
                    // Fallback "EUR" para defenderse de servicios legacy sin currency seteado.
                    Currency = searchService.Currency ?? "EUR",
                    CreatedAt = DateTime.UtcNow,
                    ExpertAvailabilityId = currentAvailability?.Id, // Guardar la disponibilidad usada
                    ExpertTimezone = expertTimezone, // ✅ INTERNACIONALIZACIÓN: Snapshot del timezone del lugar de contratación
                    ExpertCountry = expertCountry, // ✅ INTERNACIONALIZACIÓN: Snapshot del país del lugar de contratación
                    Conversations = new List<Conversation>()
                };

                _context.SearchHires.Add(searchHire);

                // ✅ NUEVO: Buscar conversación previa por SearchServiceId para migrar mensajes
                // ✅ MEJORA: Ordenar por UpdatedAt para tomar la más reciente y asegurar que esté activa
                var preHireConversation = await _context.Conversations
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.Attachments)
                    .Where(c => c.SearchServiceId == searchService.Id && 
                                             c.SearchHireId == null &&
                                             c.ClientId == search.UserId &&
                               c.ExpertId == dto.ExpertId.Value &&
                               c.IsActive == true)  // ✅ Asegurar que esté activa
                    .OrderByDescending(c => c.UpdatedAt)  // ✅ Tomar la más reciente
                    .FirstOrDefaultAsync();

                Conversation conversation;
                
                if (preHireConversation != null && preHireConversation.Messages.Any())
                {
                    // ✅ Migrar mensajes de la conversación previa a la nueva conversación
                    conversation = new Conversation
                    {
                        SearchHireId = searchHire.Id,
                        SearchServiceId = null, // Ya no es conversación previa
                        ClientId = searchHire.ClientId,
                        ExpertId = searchHire.ExpertId.Value,
                        IsActive = true,
                        CreatedAt = preHireConversation.CreatedAt, // ✅ Mantener fecha original
                        UpdatedAt = DateTime.UtcNow,
                        Messages = new List<Message>()
                    };

                    _context.Conversations.Add(conversation);
                    
                    // Migrar todos los mensajes
                    foreach (var oldMessage in preHireConversation.Messages)
                    {
                        var newMessage = new Message
                        {
                            ConversationId = conversation.Id,
                            SenderId = oldMessage.SenderId,
                            Content = oldMessage.Content,
                            SentAt = oldMessage.SentAt,
                            IsRead = oldMessage.IsRead,
                            LocationLatitude = oldMessage.LocationLatitude,
                            LocationLongitude = oldMessage.LocationLongitude
                        };
                        
                        _context.Messages.Add(newMessage);
                        
                        // Migrar attachments
                        if (oldMessage.Attachments != null && oldMessage.Attachments.Any())
                        {
                            foreach (var oldAttachment in oldMessage.Attachments)
                            {
                                var newAttachment = new MessageAttachment
                                {
                                    MessageId = newMessage.Id,
                                    Url = oldAttachment.Url,
                                    ObjectName = oldAttachment.ObjectName,
                                    Type = oldAttachment.Type,
                                    CreatedAt = oldAttachment.CreatedAt
                                };
                                _context.MessageAttachments.Add(newAttachment);
                            }
                        }
                    }
                    
                    // Marcar la conversación previa como inactiva
                    preHireConversation.IsActive = false;
                    preHireConversation.UpdatedAt = DateTime.UtcNow;
                    
                    await _loggingService.LogInfoAsync(
                        message: "Pre-hire conversation messages migrated",
                        details: $"Migrated {preHireConversation.Messages.Count} messages from pre-hire conversation {preHireConversation.Id} to new conversation {conversation.Id} for SearchHire {searchHire.Id}",
                        userId: search.UserId,
                        source: "SearchHireController.CreateSearchHire",
                        relatedEntityType: "Conversation",
                        relatedEntityId: conversation.Id,
                        additionalData: new { 
                            PreHireConversationId = preHireConversation.Id,
                            NewConversationId = conversation.Id,
                            SearchHireId = searchHire.Id,
                            MessagesMigrated = preHireConversation.Messages.Count
                        }
                    );
                }
                else
                {
                    // Crear nueva conversación sin mensajes previos
                    conversation = new Conversation
                    {
                        SearchHireId = searchHire.Id,
                        SearchServiceId = null,
                        ClientId = searchHire.ClientId,
                        ExpertId = searchHire.ExpertId.Value,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        Messages = new List<Message>()
                    };

                    _context.Conversations.Add(conversation);
                }

                searchHire.Conversations.Add(conversation);
                await _context.SaveChangesAsync();

                // ✅ Crear automáticamente la cita en estado "awaiting_appointment" con timer de 24h
                // Esto asegura que el cliente tenga 24 horas para proponer una fecha/hora
                try
                {
                    // Verificar que no exista ya una cita (por si acaso)
                    var existingAppointment = await _context.Appointments
                        .FirstOrDefaultAsync(a => a.SearchHireId == searchHire.Id);
                    
                    if (existingAppointment == null)
                    {
                        // Obtener el estado "awaiting_appointment"
                        var awaitingStatus = await _context.SystemStatuses
                            .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                      s.StatusValue == "awaiting_appointment");
                        
                        if (awaitingStatus != null)
                        {
                            // ✅ Crear Appointment sin fecha/hora/ubicación - se asignarán cuando el cliente proponga
                            var appointment = new Appointment
                            {
                                SearchHireId = searchHire.Id,
                                StatusId = awaitingStatus.Id,
                                // ProposedDate, ProposedTime, Location son nullable - se asignarán en ProposeAppointment
                                // 🌍 Round 21: snapshot del timezone del experto al crear la cita.
                                ProposerTimezone = searchHire.ExpertTimezone ?? expertTimezone ?? "Europe/Madrid",
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };

                            _context.Appointments.Add(appointment);
                            await _context.SaveChangesAsync();

                            // Crear timer para propuesta del cliente (24 horas)
                            var proposalTimer = new AppointmentTimer
                            {
                                AppointmentId = appointment.Id,
                                TimerType = "proposal",
                                StartTime = DateTime.UtcNow,
                                EndTime = DateTime.UtcNow.AddHours(24),
                                IsExpired = false,
                                CreatedAt = DateTime.UtcNow
                            };

                            _context.AppointmentTimers.Add(proposalTimer);
                            await _context.SaveChangesAsync();

                            // Programar scheduled job para cuando expire el timer (24 horas)
                            try
                            {
                                // ✅ Usar método wrapper con nombre descriptivo para Hangfire
                                var jobId = BackgroundJob.Schedule<IAppointmentService>(
                                    service => service.ProcessProposalTimerAsync(proposalTimer.Id),
                                    proposalTimer.EndTime - DateTime.UtcNow
                                );

                                // Guardar el JobId en el timer
                                proposalTimer.HangfireJobId = jobId;
                                await _context.SaveChangesAsync();

                                await _loggingService.LogInfoAsync(
                                    message: "Hangfire job programado exitosamente para timer de appointment",
                                    details: $"Timer {proposalTimer.Id} para Appointment {appointment.Id} programado. JobId: {jobId}, EndTime: {proposalTimer.EndTime}",
                                    userId: searchHire.ClientId,
                                    source: "SearchHireController.CreateSearchHire",
                                    relatedEntityType: "AppointmentTimer",
                                    relatedEntityId: proposalTimer.Id
                                );
                            }
                            catch (Exception hangfireEx)
                            {
                                // ✅ LOG: Error al programar job de Hangfire (no crítico, el timer se creó)
                                await _loggingService.LogWarningAsync(
                                    message: "Failed to schedule Hangfire job for appointment timer",
                                    details: $"Timer {proposalTimer.Id} created successfully but Hangfire job scheduling failed. Error: {hangfireEx.Message}, StackTrace: {hangfireEx.StackTrace}",
                                    userId: searchHire.ClientId,
                                    source: "SearchHireController.CreateSearchHire",
                                    relatedEntityType: "AppointmentTimer",
                                    relatedEntityId: proposalTimer.Id,
                                    additionalData: new { 
                                        TimerId = proposalTimer.Id,
                                        AppointmentId = appointment.Id,
                                        SearchHireId = searchHire.Id,
                                        Exception = hangfireEx.Message,
                                        ErrorType = hangfireEx.GetType().Name,
                                        StackTrace = hangfireEx.StackTrace,
                                        InnerException = hangfireEx.InnerException?.Message
                                    }
                                );
                                // Continuar sin el job de Hangfire - el timer se creó correctamente
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 🚨 LOG CRÍTICO: Error al crear cita automática y timer inicial
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Failed to create automatic appointment and initial timer",
                        details: $"Error creating automatic appointment for SearchHire {searchHire.Id}. " +
                                $"The SearchHire was created but the Appointment/Timer flow failed. " +
                                $"Error: {ex.Message}. StackTrace: {ex.StackTrace}",
                        userId: searchHire.ClientId,
                        source: "SearchHireController.CreateSearchHire",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHire.Id,
                        additionalData: new { 
                            Action = "CreateAutomaticAppointment",
                            SearchHireId = searchHire.Id,
                            ClientId = searchHire.ClientId,
                            ExpertId = searchHire.ExpertId,
                            Exception = ex.Message
                        },
                        notifyUser: false // No asustar al usuario, pero alertar a admins
                    );
                }

                // ✅ Notificar al cliente cuando se crea la contratación
                var client = await _context.Users.FindAsync(searchHire.ClientId);
                if (client != null)
                {
                    await _loggingService.LogInfoAsync(
                        message: "Contratación creada - Acción requerida",
                        details: $"Tu contratación #{searchHire.Id} ha sido creada. Tienes 24 horas para proponer una fecha y hora para la cita. Si no lo haces, la contratación se cancelará automáticamente.",
                        userId: searchHire.ClientId,
                        source: "SearchHireController.CreateSearchHire",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHire.Id,
                        notifyUser: true
                    );

                    // ✅ Enviar factura por email al cliente (en segundo plano con Hangfire)
                    if (!string.IsNullOrEmpty(client.Email))
                    {
                        try
                        {
                            Hangfire.BackgroundJob.Enqueue<IInvoiceService>(service => 
                                service.SendInvoiceByEmailBackgroundJob(searchHire.Id, client.Email));
                            
                            await _loggingService.LogInfoAsync(
                                message: "Factura encolada para envío por email",
                                details: $"Factura para SearchHire {searchHire.Id} encolada en Hangfire para envío a {client.Email}",
                                userId: searchHire.ClientId,
                                source: "SearchHireController.CreateSearchHire",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHire.Id
                            );
                        }
                        catch (Exception invoiceEx)
                        {
                            // ✅ LOG: Error al encolar email de factura (no crítico, la contratación se creó)
                            await _loggingService.LogWarningAsync(
                                message: "Failed to enqueue invoice email job",
                                details: $"Hangfire job enqueue failed for SearchHire {searchHire.Id}. Error: {invoiceEx.Message}. The hire was created successfully, but the invoice email will not be sent automatically.",
                                userId: searchHire.ClientId,
                                source: "SearchHireController.CreateSearchHire",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHire.Id,
                                additionalData: new { 
                                    SearchHireId = searchHire.Id,
                                    Email = client.Email,
                                    Exception = invoiceEx.Message,
                                    ErrorType = invoiceEx.GetType().Name,
                                    StackTrace = invoiceEx.StackTrace,
                                    InnerException = invoiceEx.InnerException?.Message
                                }
                            );
                        }
                    }
                }

                // ✅ Notificar al experto sobre la nueva contratación
                if (searchHire.ExpertId.HasValue && searchHire.ExpertId.Value > 0)
                {
                    await _loggingService.LogInfoAsync(
                        message: "Nueva contratación recibida",
                        details: $"Has recibido una nueva contratación #{searchHire.Id} por {searchService.Price}€. El cliente tiene 24 horas para proponer una fecha para la cita. Te avisaremos cuando lo haga.",
                        userId: searchHire.ExpertId.Value,
                        source: "SearchHireController.CreateSearchHire",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHire.Id,
                        notifyUser: true
                    );
                }

                await _loggingService.LogInfoAsync(
                    message: "Contratación creada exitosamente",
                    details: $"SearchHire {searchHire.Id} creada para Search {dto.SearchId}, Cliente {userId}, Experto {dto.ExpertId}",
                    userId: userId,
                    source: "SearchHireController.CreateSearchHire",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHire.Id
                );

                // 🌍 Round 21: aserción explícita del snapshot de currency — facilita auditar que
                // el hire heredó el currency correcto del SearchService al momento de la creación.
                await _loggingService.LogInfoAsync(
                    message: "Hire currency snapshot",
                    details: $"Hire {searchHire.Id} created with Currency={searchHire.Currency} from service {searchService.Id}",
                    userId: userId,
                    source: "SearchHireController.CreateSearchHire",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHire.Id
                );

                return CreatedAtAction(nameof(GetSearchHire), new { id = searchHire.Id }, searchHire);
            }
            catch (Exception ex)
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                int? userId = null;
                if (int.TryParse(userIdClaim, out int parsedUserId))
                {
                    userId = parsedUserId;
                }

                await _loggingService.LogErrorAsync(
                    message: "Error al crear contratación",
                    details: $"Error creando SearchHire. SearchId: {dto?.SearchId}, ExpertId: {dto?.ExpertId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId,
                    source: "SearchHireController.CreateSearchHire",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: null,
                    additionalData: new { 
                        SearchId = dto?.SearchId,
                        ExpertId = dto?.ExpertId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                return StatusCode(500, new { message = "Failed to create search hire" });
            }
        }

        // GET: api/searchhire/{id}
        [HttpGet("{id}")]
        public async Task<IActionResult> GetSearchHire(int id)
        {
            try
            {
                // 🛡️ R8 FIX: ownership check. Sin esto, cualquier usuario autenticado podía
                // leer cualquier SearchHire (conversaciones, montos, expert/client info).
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }
                var isAdmin = _authService.IsAdmin(User);

                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Search)
                    .Include(sh => sh.Conversations)
                    .FirstOrDefaultAsync(sh => sh.Id == id);

                if (searchHire == null)
                {
                    return NotFound(new { message = "Search hire not found" });
                }

                if (!isAdmin && searchHire.ClientId != userId && searchHire.ExpertId != userId)
                {
                    return Forbid();
                }

                return Ok(searchHire);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to retrieve search hire" });
            }
        }

        /// <summary>
        /// Obtener detalles completos de una contratación directamente por SearchHireId
        /// Funciona incluso cuando el Search fue eliminado (cliente borró su cuenta)
        /// </summary>
        [HttpGet("{id}/details-complete")]
        public async Task<IActionResult> GetSearchHireDetailsComplete(int id)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Cargar SearchHire con todas las relaciones necesarias (sin depender de Search)
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client) // ✅ Puede ser null si cliente borró cuenta
                    .Include(sh => sh.Expert) // ✅ Puede ser null si experto borró cuenta
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ExpertProfile)
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                        .ThenInclude(st => st.ServiceTypeCategory)
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.SelectedDeliverableTypes)
                        .ThenInclude(ssdt => ssdt.DeliverableType)
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.Images)
                    .Include(sh => sh.Appointment)
                        .ThenInclude(a => a.Status)
                    .Include(sh => sh.Appointment)
                        .ThenInclude(a => a.Timers)
                    .Include(sh => sh.Deliverables)
                    .Include(sh => sh.Disputes)
                    .Include(sh => sh.Conversations)
                        .ThenInclude(c => c.Messages)
                    .Include(sh => sh.Search) // ✅ Incluir Search si existe (puede ser null)
                        .ThenInclude(s => s.SearchParameters)
                    .FirstOrDefaultAsync(sh => sh.Id == id &&
                        (sh.ClientId == userId || 
                         (sh.ExpertId.HasValue && sh.ExpertId.Value == userId) || 
                         _authService.IsAdmin(User)));

                if (searchHire == null)
                {
                    return NotFound(new { message = "Search hire not found or unauthorized" });
                }

                // Obtener configuración de distribución de dinero
                // ✅ CORRECCIÓN: Si hay Appointment con estado de finalización, usar ese estado (más específico)
                // El estado del Appointment tiene porcentajes más precisos que el estado genérico del SearchHire
                var systemStatusService = HttpContext.RequestServices.GetRequiredService<SystemStatusService>();
                string statusValueForMoneyDistribution = searchHire.Status.StatusValue;
                
                if (searchHire?.Appointment?.Status != null && 
                    searchHire.Appointment.Status.IsFinalizationStatus)
                {
                    // Usar el estado del Appointment si es de finalización (más específico)
                    statusValueForMoneyDistribution = searchHire.Appointment.Status.StatusValue;
                }
                
                var moneyDistribution = await systemStatusService.GetMoneyDistributionConfigAsync(
                    statusValueForMoneyDistribution, 
                    searchHire.SearchService?.CategoryId, 
                    searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);

                // Obtener la categoría del servicio
                CategoryDto category = null;
                if (searchHire.SearchService?.ServiceType?.ServiceTypeCategory != null)
                {
                    category = new CategoryDto
                    {
                        Id = searchHire.SearchService.ServiceType.ServiceTypeCategory.Id,
                        Name = searchHire.SearchService.ServiceType.ServiceTypeCategory.Name,
                        IsActive = searchHire.SearchService.ServiceType.ServiceTypeCategory.IsActive,
                        CreatedAt = searchHire.SearchService.ServiceType.ServiceTypeCategory.CreatedAt,
                        UpdatedAt = searchHire.SearchService.ServiceType.ServiceTypeCategory.UpdatedAt
                    };
                }

                // Obtener la reseña si existe
                ReviewDto review = null;
                var reviewEntity = await _context.Reviews
                    .Include(r => r.Reviewer)
                    .Include(r => r.ImagesCollection)
                    .Include(r => r.SearchHire) // ✅ INTERNACIONALIZACIÓN: Cargar SearchHire para obtener ExpertCountry
                    .FirstOrDefaultAsync(r => r.SearchHireId == searchHire.Id);

                if (reviewEntity != null)
                {
                    review = new ReviewDto
                    {
                        Id = reviewEntity.Id,
                        Score = reviewEntity.Score,
                        Description = reviewEntity.Description,
                        CreatedAt = reviewEntity.CreatedAt,
                        Reviewer = reviewEntity.ReviewerId.HasValue && reviewEntity.Reviewer != null ? new UserDto
                        {
                            Id = reviewEntity.Reviewer!.Id, // ✅ Null-forgiving operator: ya verificamos que no es null
                            Name = reviewEntity.Reviewer!.Name,
                            Email = reviewEntity.Reviewer!.Email,
                            ProfilePictureUrl = null
                        } : null,
                        ImageUrls = reviewEntity.ImagesCollection?
                            .Select(img => ResolveReviewImageUrl(img))
                            .Where(url => !string.IsNullOrEmpty(url))
                            .ToList() ?? new List<string>(),
                        // ✅ INTERNACIONALIZACIÓN: País donde se realizó la contratación
                        Country = reviewEntity.SearchHire?.ExpertCountry
                    };
                }

                // Cargar disponibilidad del experto si existe
                ExpertProfileDto? expertProfileDto = null;
                if (searchHire.SearchService?.ExpertProfile != null)
                {
                    var expertProfile = searchHire.SearchService.ExpertProfile;
                    var currentAvailability = await _context.ExpertAvailabilities
                        .Where(ea => ea.ExpertId == expertProfile.Id && ea.IsActive && ea.EffectiveTo == null)
                        .OrderByDescending(ea => ea.EffectiveFrom)
                        .FirstOrDefaultAsync();

                    CurrentExpertAvailabilityDto? availabilityDto = null;
                    if (currentAvailability != null)
                    {
                        var daysOfWeek = System.Text.Json.JsonSerializer.Deserialize<List<string>>(currentAvailability.DaysOfWeek) ?? new List<string>();
                        availabilityDto = new CurrentExpertAvailabilityDto
                        {
                            Id = currentAvailability.Id,
                            DaysOfWeek = daysOfWeek,
                            StartTime = currentAvailability.StartTime,
                            EndTime = currentAvailability.EndTime,
                            EffectiveFrom = currentAvailability.EffectiveFrom
                        };
                    }

                    expertProfileDto = new ExpertProfileDto
                    {
                        Id = expertProfile.Id,
                        ProfilePictureUrl = ResolveProfilePictureUrl(expertProfile),
                        Description = expertProfile.Description ?? string.Empty,
                        StripeAccountId = expertProfile.StripeAccountId,
                        CreatedAt = expertProfile.CreatedAt,
                        User = searchHire.ExpertId.HasValue && searchHire.Expert != null ? new UserDto
                        {
                            Id = searchHire.Expert.Id,
                            Name = searchHire.Expert.Name,
                            Email = searchHire.Expert.Email,
                            ProfilePictureUrl = null
                        } : null,
                        Reviews = new List<ReviewDto>(),
                        Latitude = expertProfile.Latitude ?? string.Empty,
                        Longitude = expertProfile.Longitude ?? string.Empty,
                        StripeStatus = expertProfile.StripeStatus,
                        StripeStatusDetails = expertProfile.StripeStatusDetails,
                        OnboardingCompleted = expertProfile.OnboardingCompleted,
                        IsOnVacation = expertProfile.IsOnVacation,
                        CurrentAvailability = availabilityDto,
                        StripeFutureRequirements = expertProfile.StripeFutureRequirements,
                        StripeFutureDueAt = expertProfile.StripeFutureDueAt,
                        // ✅ INTERNACIONALIZACIÓN: Timezone y país del experto
                        Timezone = expertProfile.Timezone,
                        Country = expertProfile.Country
                    };
                }

                // ✅ SearchHire siempre se expone (aunque Search haya sido eliminado)
                SearchHireDto? searchHireDto = searchHire.Status != null ? new SearchHireDto
                {
                    Id = searchHire.Id,
                    ExpertId = searchHire.ExpertId ?? 0,
                    Status = searchHire.Status.StatusValue,
                    StatusTranslated = SearchHireStatusExtensions.ToSpanishTranslation(searchHire.Status.StatusValue),
                    CreatedAt = searchHire.CreatedAt,
                    Amount = searchHire.Amount,
                    BaseAmount = searchHire.BaseAmount,
                    TaxAmount = searchHire.TaxAmount,
                    // Round 24: currency real del cargo, snapshot inmutable (Round 21).
                    ChargeCurrency = string.IsNullOrEmpty(searchHire.Currency) ? "EUR" : searchHire.Currency,
                    ExpertTimezone = searchHire.ExpertTimezone,
                    ExpertCountry = searchHire.ExpertCountry,
                    // 🛡️ Round 10 — P-C FIX (V8 snapshots): exponer al frontend para que use
                    // el reparto pactado al crear la hire, ignorando cambios de admin posteriores.
                    ClientPercentageSnapshot = searchHire.ClientPercentageSnapshot,
                    ExpertPercentageSnapshot = searchHire.ExpertPercentageSnapshot,
                    PlatformPercentageSnapshot = searchHire.PlatformPercentageSnapshot,
                    Expert = searchHire.ExpertId.HasValue && searchHire.Expert != null ? new UserDto
                    {
                        Id = searchHire.Expert.Id,
                        Name = searchHire.Expert.Name,
                        Email = searchHire.Expert.Email,
                        ProfilePictureUrl = ResolveProfilePictureUrl(searchHire.SearchService?.ExpertProfile)
                    } : null,
                    Service = searchHire.SearchService != null ? new ServiceInfo
                    {
                        Id = searchHire.SearchService.Id,
                        ServiceTypeId = searchHire.SearchService.ServiceTypeId,
                        ServiceTypeName = searchHire.SearchService.ServiceType?.Name ?? string.Empty,
                        ServiceTypeCategoryId = searchHire.SearchService.ServiceType?.ServiceTypeCategoryId,
                        ServiceTypeCategoryName = searchHire.SearchService.ServiceType?.ServiceTypeCategory?.Name,
                        RequiresAppointment = false,
                        Price = searchHire.SearchService.Price,
                        // Round 24: poblar Currency (default 'EUR' si null).
                        Currency = string.IsNullOrEmpty(searchHire.SearchService.Currency) ? "EUR" : searchHire.SearchService.Currency,
                        ExpertLatitude = searchHire.SearchService.ExpertProfile?.Latitude,
                        ExpertLongitude = searchHire.SearchService.ExpertProfile?.Longitude,
                        LocationRange = searchHire.Search?.SearchParameters?.FirstOrDefault()?.LocationRange ?? 50
                    } : null,
                    StatusInfo = new SystemStatusDto
                    {
                        Id = searchHire.Status.Id,
                        StatusType = searchHire.Status.StatusType,
                        StatusName = searchHire.Status.StatusName,
                        StatusValue = searchHire.Status.StatusValue,
                        DisplayName = searchHire.Status.DisplayName,
                        Description = searchHire.Status.Description,
                        Color = searchHire.Status.Color,
                        IsActive = searchHire.Status.IsActive,
                        IsFinalizationStatus = searchHire.Status.IsFinalizationStatus,
                        SortOrder = searchHire.Status.SortOrder,
                        CreatedAt = searchHire.Status.CreatedAt,
                        UpdatedAt = searchHire.Status.UpdatedAt
                    }
                } : null;

                // Crear respuesta completa
                var searchDetailsComplete = new SearchDetailsCompleteResponseDto
                {
                    // ✅ Incluir SearchHire aunque la entidad Search haya sido eliminada
                    Search = searchHireDto != null ? new SearchListDto
                    {
                        Id = searchHire.Search?.Id ?? 0,
                        UserId = searchHire.Search?.UserId ?? searchHire.ClientId ?? 0,
                        Title = searchHire.Search?.Title ?? string.Empty,
                        Description = searchHire.Search?.Description ?? string.Empty,
                        Frequency = searchHire.Search?.Frequency ?? 0,
                        IsActive = searchHire.Search?.IsActive ?? false,
                        IsRevised = searchHire.Search?.IsRevised ?? false,
                        CreatedAt = searchHire.Search?.CreatedAt ?? searchHire.CreatedAt,
                        User = searchHire.ClientId.HasValue && searchHire.Client != null ? new UserDto
                        {
                            Id = searchHire.Client.Id,
                            Name = searchHire.Client.Name,
                            Email = searchHire.Client.Email,
                            ProfilePictureUrl = null
                        } : null,
                        SearchHire = searchHireDto
                    } : null,
                    MoneyDistribution = moneyDistribution != null ? new MoneyDistributionConfigDto
                    {
                        ClientPercentage = moneyDistribution.ClientPercentage,
                        ExpertPercentage = moneyDistribution.ExpertPercentage,
                        PlatformPercentage = moneyDistribution.PlatformPercentage,
                        Source = "SearchHire",
                        Status = "Active"
                    } : null,
                    Category = category,
                    Review = review,
                    Appointment = searchHire.Appointment != null ? new AppointmentDto
                    {
                        Id = searchHire.Appointment.Id,
                        SearchHireId = searchHire.Appointment.SearchHireId,
                        Status = searchHire.Appointment.Status?.StatusValue ?? string.Empty,
                        ProposedDate = searchHire.Appointment.ProposedDate, // ✅ Nullable
                        ProposedTime = searchHire.Appointment.ProposedTime, // ✅ Nullable
                        Location = searchHire.Appointment.Location, // ✅ Nullable
                        Latitude = searchHire.Appointment.Latitude,
                        Longitude = searchHire.Appointment.Longitude,
                        DoorNumber = searchHire.Appointment.DoorNumber,
                        OwnerPhone = searchHire.Appointment.OwnerPhone,
                        SiteDetails = searchHire.Appointment.SiteDetails,
                        RejectionCount = searchHire.Appointment.RejectionCount,
                        ClientCancellationCount = searchHire.Appointment.ClientCancellationCount,
                        ExpertCancellationCount = searchHire.Appointment.ExpertCancellationCount,
                        LastRejectionAt = searchHire.Appointment.LastRejectionAt,
                        LastClientCancellationAt = searchHire.Appointment.LastClientCancellationAt,
                        LastExpertCancellationAt = searchHire.Appointment.LastExpertCancellationAt,
                        LastProposalAt = searchHire.Appointment.LastProposalAt,
                        LastResponseAt = searchHire.Appointment.LastResponseAt,
                        CreatedAt = searchHire.Appointment.CreatedAt,
                        UpdatedAt = searchHire.Appointment.UpdatedAt,
                        ClientName = searchHire.ClientId.HasValue && searchHire.Client != null ? searchHire.Client.Name : null,
                        ExpertName = searchHire.ExpertId.HasValue && searchHire.Expert != null ? searchHire.Expert.Name : null,
                        Amount = searchHire.Amount,
                        ExpertLatitude = searchHire.SearchService?.ExpertProfile?.Latitude,
                        ExpertLongitude = searchHire.SearchService?.ExpertProfile?.Longitude,
                        LocationRange = searchHire.Search?.SearchParameters?.FirstOrDefault()?.LocationRange ?? 50,
                        StatusInfo = searchHire.Appointment.Status != null ? new SystemStatusDto
                        {
                            Id = searchHire.Appointment.Status.Id,
                            StatusType = searchHire.Appointment.Status.StatusType,
                            StatusName = searchHire.Appointment.Status.StatusName,
                            StatusValue = searchHire.Appointment.Status.StatusValue,
                            DisplayName = searchHire.Appointment.Status.DisplayName,
                            Description = searchHire.Appointment.Status.Description,
                            Color = searchHire.Appointment.Status.Color,
                            IsActive = searchHire.Appointment.Status.IsActive,
                            IsFinalizationStatus = searchHire.Appointment.Status.IsFinalizationStatus,
                            SortOrder = searchHire.Appointment.Status.SortOrder,
                            CreatedAt = searchHire.Appointment.Status.CreatedAt,
                            UpdatedAt = searchHire.Appointment.Status.UpdatedAt
                        } : null,
                        Timers = searchHire.Appointment.Timers?.Select(t => new AppointmentTimerDto
                        {
                            Id = t.Id,
                            AppointmentId = t.AppointmentId,
                            TimerType = t.TimerType,
                            StartTime = t.StartTime,
                            EndTime = t.EndTime,
                            IsExpired = t.IsExpired,
                            ExpiredAt = t.ExpiredAt
                        }).ToList() ?? new List<AppointmentTimerDto>()
                    } : null,
                    Deliverables = searchHire.Deliverables?.Select(d => new DeliverableDto
                    {
                        Id = d.Id,
                        Type = d.Type,
                        Url = ResolveDeliverableUrl(d),
                        CreatedAt = d.CreatedAt
                    }).ToList() ?? new List<DeliverableDto>(),
                    RequiredDeliverableTypes = searchHire.SearchService?.SelectedDeliverableTypes?
                        .Where(ssdt => ssdt.IsSelected && ssdt.DeliverableType != null)
                        .Select(ssdt => new DeliverableTypeDto
                        {
                            Id = ssdt.DeliverableType.Id,
                            Name = ssdt.DeliverableType.Name,
                            DisplayName = ssdt.DeliverableType.DisplayName,
                            Description = ssdt.DeliverableType.Description,
                            IsRequired = ssdt.DeliverableType.IsRequired,
                            IsActive = ssdt.DeliverableType.IsActive,
                            SortOrder = ssdt.DeliverableType.SortOrder
                        })
                        .OrderBy(dt => dt.SortOrder)
                        .ToList() ?? new List<DeliverableTypeDto>(),
                    Disputes = searchHire.Disputes?.Select(d => new DisputeDto
                    {
                        Id = d.Id,
                        SearchHireId = d.SearchHireId,
                        ReporterId = d.ReporterId,
                        Status = d.Status,
                        Reason = d.Reason,
                        ExpertResponse = d.ExpertResponse,
                        ExpertResponseDeadline = d.ExpertResponseDeadline,
                        ExpertResponseAt = d.ExpertResponseAt,
                        CanExpertRespond = d.CanExpertRespond,
                        CreatedAt = d.CreatedAt
                    }).ToList() ?? new List<DisputeDto>(),
                    ExpertProfile = expertProfileDto
                };

                return Ok(searchDetailsComplete);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error", detail = ex.Message });
            }
        }

        // GET: api/searchhire/client
        [HttpGet("client")]
        public async Task<IActionResult> GetClientHires([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Validar parámetros
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                var (hires, totalCount) = await _searchHireService.GetClientHires(userId, page, pageSize);
                
                return Ok(new
                {
                    hires,
                    pagination = new
                    {
                        page,
                        pageSize,
                        totalCount,
                        totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                        hasNextPage = page * pageSize < totalCount,
                        hasPreviousPage = page > 1
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to retrieve hires" });
            }
        }

        // GET: api/searchhire/expert
        [HttpGet("expert")]
        public async Task<IActionResult> GetExpertHires([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Validar parámetros
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                var (hires, totalCount) = await _searchHireService.GetExpertHires(userId, page, pageSize);
                
                return Ok(new
                {
                    hires,
                    pagination = new
                    {
                        page,
                        pageSize,
                        totalCount,
                        totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                        hasNextPage = page * pageSize < totalCount,
                        hasPreviousPage = page > 1
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to retrieve hires" });
            }
        }

        // PUT: api/searchhire/{hireId}/status
        [HttpPut("{hireId}/status")]
        public async Task<IActionResult> UpdateHireStatus(int hireId, [FromBody] UpdateSearchHireStatusDto request)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // ✅ VALIDACIÓN: Verificar si el usuario está bloqueado
                var user = await _context.Users.FindAsync(userId);
                if (user != null && user.IsBlocked)
                {
                    return Unauthorized(new { message = "User account is blocked" });
                }

                var result = await _searchHireService.UpdateHireStatus(userId, hireId, request.Status);
                if (!result.Success)
                {
                    return BadRequest(new { message = result.ErrorMessage });
                }

                return Ok(new { message = "Status updated successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to update status" });
            }
        }

        [HttpPost("complete-service")]
        public async Task<IActionResult> CompleteService([FromBody] CompleteServiceDto request)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Validaciones que NO dependen del searchHire — se hacen fuera de la tx.
                if (request.ClientApproved == null)
                {
                    return BadRequest(new { error = "ClientApproved is required" });
                }

                // 🔧 FIX (medio-alto): este endpoint SOLO aprueba/completa. El rechazo/disputa NO se gestiona aquí.
                // Antes, ClientApproved=false ponía el hire en 'disputed' SIN crear la fila Disputes → hire zombi
                // (dinero capturado congelado, invisible para el admin que resuelve desde la tabla Disputes) que
                // ADEMÁS bloqueaba la disputa real: CreateDisputeWithFiles exige estado Completed/AwaitingClientDecision
                // y rechaza 'disputed'. El frontend nunca llama con false (usa POST /api/Dispute/dispute-service,
                // que sí crea la disputa con motivo + adjuntos); este guard cierra la trampa ante llamadas directas
                // a la API. La rama if(!ClientApproved) de abajo queda inalcanzable a propósito.
                if (!request.ClientApproved.Value)
                {
                    return BadRequest(new { error = "Para rechazar o disputar el servicio, usa el endpoint de disputa (POST /api/Dispute/dispute-service)." });
                }

                // ✅ USAR EXECUTION STRATEGY para compatibilidad con NpgsqlRetryingExecutionStrategy
                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    // 🛡️ D2 FIX: declarado fuera del try para que los catch blocks
                    // (StripeException/Exception) puedan loguear datos del hire (con ?. null-safe).
                    SearchHire? searchHire = null;
                    try
                    {
                        // 🛡️ D2 FIX: FOR UPDATE DENTRO de la transacción. Antes estaba fuera, así que el lock
                        // se liberaba antes del BeginTransaction (PostgreSQL: FOR UPDATE solo persiste DENTRO
                        // de una tx explícita). Resultado: dos requests concurrentes podían pasar las
                        // validaciones a la vez → doble money distribution al experto. Ahora el lock vive
                        // hasta el CommitAsync.
                        searchHire = await _context.SearchHires
                            .FromSqlInterpolated($"SELECT *, xmin FROM \"SearchHires\" WHERE \"Id\" = {request.SearchHireId} FOR UPDATE")
                            .Include(sh => sh.Status)
                            .Include(sh => sh.Expert)
                                .ThenInclude(e => e.ExpertProfile)
                            .Include(sh => sh.Client)
                            .FirstOrDefaultAsync();

                        if (searchHire == null)
                        {
                            await transaction.RollbackAsync();
                            return (IActionResult)NotFound(new { message = "Service not found" });
                        }

                        if (searchHire.ClientId != userId)
                        {
                            await transaction.RollbackAsync();
                            return Unauthorized(new { message = "Unauthorized to complete this service" });
                        }

                        if (searchHire.Status.StatusValue != SearchHireStatus.Pending.ToStringValue() &&
                            searchHire.Status.StatusValue != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                        {
                            await transaction.RollbackAsync();
                            return BadRequest(new { message = "Service cannot be approved in current state" });
                        }

                        searchHire.ClientApproved = request.ClientApproved.Value;

                        if (!searchHire.ClientApproved.Value)
                        {
                            // ✅ DISPUTA: Cliente rechaza servicio → Abrir disputa para revisión admin
                            var disputedStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Disputed.ToStringValue());
                            searchHire.StatusId = disputedStatusId;
                            searchHire.UpdatedAt = DateTime.UtcNow;
                        }
                        else
                        {
                            bool ok;
                            try
                            {
                                ok = await _refundService.ProcessMoneyDistributionAsync(
                                    searchHire.Id,
                                    SearchHireStatus.Completed.ToStringValue(),
                                    "Client approved service",
                                    userId);
                            }
                            catch (Exception moneyEx)
                            {
                                // 🔁 A4: si la distribución LANZA (no solo devuelve false) NO propagar al catch
                                // externo (rollback+500 dejaría al cliente atascado tras aprobar). Se trata
                                // igual que ok==false: se completa y se reintenta el dinero async (idempotente).
                                ok = false;
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Money distribution threw on service completion - completing anyway, retry enqueued",
                                    details: $"SearchHire {searchHire.Id}: ProcessMoneyDistributionAsync threw ({moneyEx.GetType().Name}: {moneyEx.Message}); marking Completed and retrying expert payment asynchronously.",
                                    userId: userId,
                                    source: "SearchHireController.CompleteService",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHire.Id);
                            }
                            if (!ok)
                            {
                                // 🔁 FRENTE 6: NO bloquear ni hacer rollback. El cliente aprobó: el servicio se
                                // marca Completed igualmente y se encola el reintento del pago al experto
                                // (idempotente). Así el usuario NO queda atascado por un fallo de Stripe; el
                                // admin ya fue avisado (Critical → email) por el servicio de dinero.
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Expert payment failed on service completion - completing anyway, money retry enqueued",
                                    details: $"SearchHire {searchHire.Id} approved by client but money distribution failed; marking Completed and retrying the expert payment asynchronously.",
                                    userId: userId,
                                    source: "SearchHireController.CompleteService",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHire.Id);
                                Hangfire.BackgroundJob.Schedule<StripeRefundService>(
                                    s => s.RetryMoneyDistributionJobAsync(
                                        searchHire.Id,
                                        SearchHireStatus.Completed.ToStringValue(),
                                        "Retry expert payment on service completion (money pending)",
                                        userId),
                                    TimeSpan.FromMinutes(2));
                            }

                            var completedStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Completed.ToStringValue());
                            searchHire.StatusId = completedStatusId;
                            searchHire.UpdatedAt = DateTime.UtcNow;
                        }
                        
                        // ✅ Cancelar timer de client_decision cuando el cliente aprueba o disputa
                        var appointment = await _context.Appointments
                            .FirstOrDefaultAsync(a => a.SearchHireId == searchHire.Id);
                        
                        if (appointment != null)
                        {
                            var clientDecisionTimers = await _context.AppointmentTimers
                                .Where(t => t.AppointmentId == appointment.Id && 
                                           t.TimerType == "client_decision" && 
                                           !t.IsExpired)
                                .ToListAsync();
                            
                            foreach (var timer in clientDecisionTimers)
                            {
                                timer.IsExpired = true;
                                timer.ExpiredAt = DateTime.UtcNow;
                                
                                // Cancelar job de Hangfire si existe
                                if (!string.IsNullOrEmpty(timer.HangfireJobId))
                                {
                                    try
                                    {
                                        BackgroundJob.Delete(timer.HangfireJobId);
                                        timer.HangfireJobId = null;
                                    }
                                    catch
                                    {
                                        timer.HangfireJobId = null;
                                    }
                                }
                            }

                            // ✅ FRENTE 8 (c): si el cliente APROBÓ, la cita también termina → marcarla
                            // completada para que cita y contratación no queden desincronizadas (antes el
                            // hire pasaba a 'completed' pero la cita se quedaba abierta). En rechazo NO se
                            // toca (se abre disputa y la cita se cierra al resolverla).
                            if (searchHire.ClientApproved.Value)
                            {
                                var apptCompleted = await _context.SystemStatuses
                                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus"
                                                              && s.StatusValue == "appointment_completed");
                                if (apptCompleted != null)
                                {
                                    appointment.StatusId = apptCompleted.Id;
                                    appointment.UpdatedAt = DateTime.UtcNow;
                                }
                            }
                        }

                        await _context.SaveChangesAsync();
                        await transaction.CommitAsync();

                        // ✅ Notificar a cliente y experto según el resultado
                        if (searchHire.ClientApproved.Value)
                        {
                            // Cliente aprobó - notificar a ambos
                            await _loggingService.LogInfoAsync(
                                message: "Servicio completado",
                                details: $"Has aprobado el servicio #{searchHire.Id}. El experto recibirá el pago.",
                                userId: searchHire.ClientId,
                                source: "SearchHireController.CompleteService",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHire.Id,
                                notifyUser: true
                            );

                            if (searchHire.ExpertId.HasValue)
                            {
                                await _loggingService.LogInfoAsync(
                                    message: "Servicio aprobado por el cliente",
                                    details: $"El cliente ha aprobado tu servicio #{searchHire.Id}. Has recibido {searchHire.Amount:F2}€.",
                                    userId: searchHire.ExpertId.Value,
                                    source: "SearchHireController.CompleteService",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHire.Id,
                                    notifyUser: true
                                );
                            }
                        }
                        else
                        {
                            // Cliente rechazó - abrir disputa - notificar a ambos
                            await _loggingService.LogWarningAsync(
                                message: "Disputa abierta",
                                details: $"Has rechazado el servicio #{searchHire.Id}. Se ha abierto una disputa para revisión.",
                                userId: searchHire.ClientId,
                                source: "SearchHireController.CompleteService",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHire.Id,
                                notifyUser: true
                            );

                            if (searchHire.ExpertId.HasValue)
                            {
                                await _loggingService.LogWarningAsync(
                                    message: "Disputa abierta por el cliente",
                                    details: $"El cliente ha rechazado el servicio #{searchHire.Id} y se ha abierto una disputa. Un administrador la revisará.",
                                    userId: searchHire.ExpertId.Value,
                                    source: "SearchHireController.CompleteService",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHire.Id,
                                    notifyUser: true
                                );
                            }
                        }

                        return Ok(new { message = searchHire.ClientApproved.Value ? "Service completed" : "Dispute opened" });
                    }
                    catch (StripeException ex)
                    {
                        await transaction.RollbackAsync();
                        // 🔒 LOG CRÍTICO: Error de Stripe durante completado de servicio (una sola vez, antes de ProcessMoneyDistributionAsync)
                        // Este error ocurre ANTES de llamar a ProcessMoneyDistributionAsync, por lo que debe loguearse aquí
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Stripe error during service completion - before money distribution",
                            details: $"Stripe exception occurred in CompleteService endpoint before calling ProcessMoneyDistributionAsync for SearchHire {searchHire?.Id}. " +
                                    $"Client {userId} approved service, but Stripe operation failed. " +
                                    $"Stripe Error: {ex.Message}, Type: {ex.StripeError?.Type}, Code: {ex.StripeError?.Code}, DeclineCode: {ex.StripeError?.DeclineCode}. " +
                                    $"SearchHire Status: {searchHire?.Status?.StatusValue}, Amount: {searchHire?.Amount}€, ExpertId: {searchHire?.ExpertId}. " +
                                    $"ACTION REQUIRED: Review Stripe error and retry service completion if applicable.",
                            userId: userId,
                            source: "SearchHireController.CompleteService",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHire?.Id,
                            additionalData: new {
                                SearchHireId = searchHire?.Id,
                                ExpertId = searchHire?.ExpertId,
                                Amount = searchHire?.Amount,
                                Status = searchHire?.Status?.StatusValue,
                                StripeError = ex.Message,
                                StripeErrorType = ex.StripeError?.Type,
                                StripeErrorCode = ex.StripeError?.Code,
                                StripeDeclineCode = ex.StripeError?.DeclineCode,
                                StripeParam = ex.StripeError?.Param
                            }
                        );

                        return StatusCode(500, new { message = "Failed to process payment to expert" });
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        // 🔒 LOG CRÍTICO: Error general durante completado de servicio (una sola vez, con información completa)
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Unexpected error completing service",
                            details: $"An unexpected exception occurred while completing service for SearchHire {searchHire?.Id}. " +
                                    $"Client {userId} attempted to approve service (ClientApproved: {request.ClientApproved}). " +
                                    $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                                    $"SearchHire Status: {searchHire?.Status?.StatusValue}, Amount: {searchHire?.Amount}€, ExpertId: {searchHire?.ExpertId}, ClientId: {searchHire?.ClientId}. " +
                                    $"Stack Trace: {ex.StackTrace}. " +
                                    $"ACTION REQUIRED: Review error details and retry if applicable.",
                            userId: userId,
                            source: "SearchHireController.CompleteService",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHire?.Id,
                            additionalData: new {
                                SearchHireId = searchHire?.Id,
                                ExpertId = searchHire?.ExpertId,
                                ClientId = searchHire?.ClientId,
                                Amount = searchHire?.Amount,
                                Status = searchHire?.Status?.StatusValue,
                                ClientApproved = request.ClientApproved,
                                ErrorType = ex.GetType().Name,
                                ErrorMessage = ex.Message,
                                StackTrace = ex.StackTrace,
                                InnerException = ex.InnerException?.Message
                            }
                        );
                        
                        return StatusCode(500, new { message = "Failed to complete service" });
                    }
                });
            }
            catch (Exception ex)
            {
                // 🚨 LOG CRÍTICO: Error general fuera de la transacción (una sola vez, con información completa)
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                int? userId = null;
                if (int.TryParse(userIdClaim, out int parsedUserId))
                {
                    userId = parsedUserId;
                }
                
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Error in CompleteService endpoint - outer catch",
                    details: $"An unexpected exception occurred in CompleteService endpoint before entering transaction. " +
                            $"Request: SearchHireId={request?.SearchHireId}, ClientApproved={request?.ClientApproved}. " +
                            $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                            $"User Context: userId={userId}, userIdClaim={userIdClaim}. " +
                            $"Stack Trace: {ex.StackTrace}. " +
                            $"ACTION REQUIRED: Review error - this indicates a pre-transaction validation or data loading issue.",
                    userId: userId,
                    source: "SearchHireController.CompleteService",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: request?.SearchHireId ?? 0,
                    additionalData: new { 
                        SearchHireId = request?.SearchHireId,
                        ClientApproved = request?.ClientApproved,
                        UserIdClaim = userIdClaim,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );
                
                return StatusCode(500, new { message = "Failed to complete service" });
            }
        }

        private async Task ExpireClientDecisionTimersAsync(int searchHireId)
        {
            var appointment = await _context.Appointments
                .FirstOrDefaultAsync(a => a.SearchHireId == searchHireId);

            if (appointment == null)
            {
                return;
            }

            var clientDecisionTimers = await _context.AppointmentTimers
                .Where(t => t.AppointmentId == appointment.Id &&
                            t.TimerType == "client_decision" &&
                            !t.IsExpired)
                .ToListAsync();

            if (clientDecisionTimers.Count == 0)
            {
                return;
            }

            foreach (var timer in clientDecisionTimers)
            {
                timer.IsExpired = true;
                timer.ExpiredAt = DateTime.UtcNow;

                if (!string.IsNullOrEmpty(timer.HangfireJobId))
                {
                    try
                    {
                        BackgroundJob.Delete(timer.HangfireJobId);
                        timer.HangfireJobId = null;
                    }
                    catch
                    {
                        timer.HangfireJobId = null;
                    }
                }
            }

            await _context.SaveChangesAsync();
        }

        private string ResolveProfilePictureUrl(ExpertProfile? expertProfile)
        {
            if (expertProfile == null)
            {
                return "/default-avatar.png";
            }

            var fallback = string.IsNullOrWhiteSpace(expertProfile.ProfilePictureUrl)
                ? "/default-avatar.png"
                : expertProfile.ProfilePictureUrl;

            return _signedUrlService.GetSignedUrl(expertProfile.ProfilePictureObjectName ?? string.Empty) ?? fallback;
        }

        private string ResolveReviewImageUrl(ReviewImage? image)
        {
            if (image == null)
            {
                return string.Empty;
            }

            // ✅ PRIORIDAD 1: Si la URL es de Unsplash u otro servicio externo, usar directamente (IGNORAR ImageObjectName)
            if (!string.IsNullOrWhiteSpace(image.ImageUrl))
            {
                var imageUrlLower = image.ImageUrl.ToLowerInvariant();
                if (imageUrlLower.Contains("unsplash.com") || 
                    imageUrlLower.Contains("pexels.com") || 
                    imageUrlLower.Contains("picsum.photos") ||
                    imageUrlLower.Contains("http://") ||
                    imageUrlLower.Contains("https://"))
                {
                    // Si es URL externa, NO usar ImageObjectName para generar signed URL
                    return image.ImageUrl;
                }
            }

            // ✅ PRIORIDAD 2: Si no es URL externa y hay ImageObjectName, intentar generar URL firmada del bucket
            if (!string.IsNullOrWhiteSpace(image.ImageObjectName))
            {
                var signedUrl = _signedUrlService.GetSignedUrl(image.ImageObjectName);
                if (!string.IsNullOrWhiteSpace(signedUrl))
                {
                    return signedUrl;
                }
            }

            // ✅ PRIORIDAD 3: Fallback a ImageUrl si existe
            return string.IsNullOrWhiteSpace(image.ImageUrl) ? string.Empty : image.ImageUrl;
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
    }

    public class CompleteServiceDto
    {
        public int SearchHireId { get; set; }
        public bool? ClientApproved { get; set; }
    }

    public class CreateSearchHireDto
    {
        public int SearchId { get; set; }
        public int? ExpertId { get; set; }
    }
}