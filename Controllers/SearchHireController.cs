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

        public SearchHireController(
            SearchHireService searchHireService,
            AppDbContext context,

            IConfiguration configuration,
            IAuthorizationServices authService,
            StripeRefundService refundService,
            ILoggingService loggingService,
            IInvoiceService invoiceService,
            IAppointmentService appointmentService)
        {
            _searchHireService = searchHireService;
            _context = context;
            _configuration = configuration;
            _authService = authService;
            _refundService = refundService;
            _loggingService = loggingService;
            _invoiceService = invoiceService;
            _appointmentService = appointmentService;
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
                // Default to "pending" (ID = 1)
                return 1;
            }
            
            return systemStatus.Id;
        }

        // POST: api/searchhire
        [HttpPost]
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

                var searchHire = new SearchHire
                {
                    SearchId = dto.SearchId,
                    ClientId = search.UserId,
                    ExpertId = dto.ExpertId.Value,
                    SearchServiceId = searchService.Id,
                    StatusId = await GetStatusIdByValueAsync("pending"),
                    Amount = searchService.Price,
                    CreatedAt = DateTime.UtcNow,
                    ExpertAvailabilityId = currentAvailability?.Id, // Guardar la disponibilidad usada
                    Conversations = new List<Conversation>()
                };

                _context.SearchHires.Add(searchHire);

                // Automatically create a Conversation
                var conversation = new Conversation
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
                            var appointment = new Appointment
                            {
                                SearchHireId = searchHire.Id,
                                StatusId = awaitingStatus.Id,
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
                            var jobId = BackgroundJob.Schedule<IAppointmentService>(
                                service => service.ProcessAppointmentTimerAsync(proposalTimer.Id),
                                proposalTimer.EndTime - DateTime.UtcNow
                            );

                            // Guardar el JobId en el timer
                            proposalTimer.HangfireJobId = jobId;
                            await _context.SaveChangesAsync();
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log error pero no fallar la creación de la contratación
                    await _loggingService.LogWarningAsync(
                        message: "Failed to create automatic appointment",
                        details: $"Error creating automatic appointment for SearchHire {searchHire.Id}: {ex.Message}",
                        userId: searchHire.ClientId,
                        source: "SearchHireController.CreateSearchHire",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHire.Id,
                        notifyUser: false
                    );
                }

                // ✅ Notificar al cliente cuando se crea la contratación
                var client = await _context.Users.FindAsync(searchHire.ClientId);
                if (client != null)
                {
                    await _loggingService.LogInfoAsync(
                        message: "Contratación creada",
                        details: $"Tu contratación #{searchHire.Id} ha sido creada exitosamente. El experto ha sido notificado.",
                        userId: searchHire.ClientId,
                        source: "SearchHireController.CreateSearchHire",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHire.Id,
                        notifyUser: true
                    );

                    // ✅ Enviar factura por email al cliente (en segundo plano con Hangfire)
                    if (!string.IsNullOrEmpty(client.Email))
                    {
                        Hangfire.BackgroundJob.Enqueue<IInvoiceService>(service => 
                            service.SendInvoiceByEmailBackgroundJob(searchHire.Id, client.Email));
                        Console.WriteLine($"[SEARCH HIRE CONTROLLER] [INVOICE] Factura encolada para envío. SearchHireId: {searchHire.Id}, Email: {client.Email}");
                    }
                }

                // ✅ Notificar al experto sobre la nueva contratación
                if (searchHire.ExpertId.HasValue && searchHire.ExpertId.Value > 0)
                {
                    await _loggingService.LogInfoAsync(
                        message: "Nueva contratación recibida",
                        details: $"Has recibido una nueva contratación #{searchHire.Id} por {searchService.Price}€. Revisa los detalles y contacta con el cliente.",
                        userId: searchHire.ExpertId.Value,
                        source: "SearchHireController.CreateSearchHire",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHire.Id,
                        notifyUser: true
                    );
                }

                return CreatedAtAction(nameof(GetSearchHire), new { id = searchHire.Id }, searchHire);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to create search hire" });
            }
        }

        // GET: api/searchhire/{id}
        [HttpGet("{id}")]
        public async Task<IActionResult> GetSearchHire(int id)
        {
            try
            {
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Search)
                    .Include(sh => sh.Conversations)
                    .FirstOrDefaultAsync(sh => sh.Id == id);

                if (searchHire == null)
                {
                    return NotFound(new { message = "Search hire not found" });
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
                var systemStatusService = HttpContext.RequestServices.GetRequiredService<SystemStatusService>();
                var moneyDistribution = await systemStatusService.GetMoneyDistributionAsync(
                    searchHire.Status.StatusValue, 
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
                        ImageUrls = reviewEntity.ImagesCollection?.Select(img => img.ImageUrl).ToList() ?? new List<string>()
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
                        ProfilePictureUrl = expertProfile.ProfilePictureUrl ?? string.Empty,
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
                        StripeFutureDueAt = expertProfile.StripeFutureDueAt
                    };
                }

                // Crear respuesta completa
                var searchDetailsComplete = new SearchDetailsCompleteResponseDto
                {
                    Search = searchHire.Search != null ? new SearchListDto
                    {
                        Id = searchHire.Search.Id,
                        UserId = searchHire.Search.UserId,
                        Title = searchHire.Search.Title,
                        Description = searchHire.Search.Description,
                        Frequency = searchHire.Search.Frequency,
                        IsActive = searchHire.Search.IsActive,
                        IsRevised = searchHire.Search.IsRevised,
                        CreatedAt = searchHire.Search.CreatedAt,
                        User = searchHire.ClientId.HasValue && searchHire.Client != null ? new UserDto
                        {
                            Id = searchHire.Client.Id,
                            Name = searchHire.Client.Name,
                            Email = searchHire.Client.Email,
                            ProfilePictureUrl = null
                        } : null
                    } : null, // ✅ Search puede ser null si cliente borró cuenta
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
                        ProposedDate = searchHire.Appointment.ProposedDate,
                        ProposedTime = searchHire.Appointment.ProposedTime,
                        Location = searchHire.Appointment.Location,
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
                        Url = d.Url,
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
        public async Task<IActionResult> GetClientHires()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var hires = await _searchHireService.GetClientHires(userId);
                return Ok(hires);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to retrieve hires" });
            }
        }

        // GET: api/searchhire/expert
        [HttpGet("expert")]
        public async Task<IActionResult> GetExpertHires()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var hires = await _searchHireService.GetExpertHires(userId);
                return Ok(hires);
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

                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var searchHire = await _context.SearchHires
                    .FromSqlInterpolated($"SELECT * FROM \"SearchHires\" WHERE \"Id\" = {request.SearchHireId} FOR UPDATE")
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .Include(sh => sh.Client)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    return NotFound(new { message = "Service not found" });
                }

                if (searchHire.ClientId != userId)
                {
                    return Unauthorized(new { message = "Unauthorized to complete this service" });
                }

                if (searchHire.Status.StatusValue != SearchHireStatus.Pending.ToStringValue() && 
                    searchHire.Status.StatusValue != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                {
                    return BadRequest(new { message = "Service cannot be approved in current state" });
                }

                if (request.ClientApproved == null)
                {
                    return BadRequest(new { error = "ClientApproved is required" });
                }

                // 🔄 USAR EXECUTION STRATEGY para compatibilidad con NpgsqlRetryingExecutionStrategy
                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    try
                    {
                        searchHire.ClientApproved = request.ClientApproved.Value;

                        if (!searchHire.ClientApproved.Value)
                        {
                            var disputedStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Disputed.ToStringValue());
                            searchHire.StatusId = disputedStatusId;
                            searchHire.UpdatedAt = DateTime.UtcNow;

                            await _context.SaveChangesAsync();
                            await ExpireClientDecisionTimersAsync(searchHire.Id);
                        }
                        else
                        {
                            await _context.SaveChangesAsync();
                            await ExpireClientDecisionTimersAsync(searchHire.Id);

                            var ok = await _refundService.ProcessMoneyDistributionAsync(
                                searchHire.Id,
                                SearchHireStatus.Completed.ToStringValue(),
                                "Client approved service",
                                userId);

                            if (!ok)
                            {
                                _context.ChangeTracker.Clear();
                                
                                var lastCriticalLog = await _context.Logs
                                    .FromSqlRaw("SELECT * FROM \"Logs\" WHERE \"RelatedEntityType\" = {0} " +
                                               "AND \"RelatedEntityId\" = {1} " +
                                               "AND \"Source\" = {2} " +
                                               "AND \"CreatedAt\" >= NOW() - INTERVAL '5 minutes' " +
                                               "ORDER BY \"CreatedAt\" DESC LIMIT 1",
                                               "SearchHire", searchHire.Id, "StripeRefundService.ProcessMoneyDistributionAsync")
                                    .AsNoTracking()
                                    .FirstOrDefaultAsync();
                                
                                return StatusCode(500, new { 
                                    message = "Failed to process payment to expert",
                                    logId = lastCriticalLog?.Id,
                                    errorMessage = lastCriticalLog?.Message,
                                    searchHireId = searchHire.Id
                                });
                            }

                            await _context.Entry(searchHire).ReloadAsync();
                        }

                        if (searchHire.ClientApproved.Value)
                        {
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
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Stripe error during service completion - before money distribution",
                            details: $"Stripe exception occurred in CompleteService endpoint before calling ProcessMoneyDistributionAsync for SearchHire {searchHire.Id}. " +
                                    $"Client {userId} approved service, but Stripe operation failed. " +
                                    $"Stripe Error: {ex.Message}, Type: {ex.StripeError?.Type}, Code: {ex.StripeError?.Code}, DeclineCode: {ex.StripeError?.DeclineCode}. " +
                                    $"SearchHire Status: {searchHire.Status?.StatusValue}, Amount: {searchHire.Amount}€, ExpertId: {searchHire.ExpertId}. " +
                                    $"ACTION REQUIRED: Review Stripe error and retry service completion if applicable.",
                            userId: userId,
                            source: "SearchHireController.CompleteService",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHire.Id,
                            additionalData: new { 
                                SearchHireId = searchHire.Id,
                                ExpertId = searchHire.ExpertId,
                                Amount = searchHire.Amount,
                                Status = searchHire.Status?.StatusValue,
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
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Unexpected error completing service",
                            details: $"An unexpected exception occurred while completing service for SearchHire {searchHire.Id}. " +
                                    $"Client {userId} attempted to approve service (ClientApproved: {request.ClientApproved}). " +
                                    $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                                    $"SearchHire Status: {searchHire.Status?.StatusValue}, Amount: {searchHire.Amount}€, ExpertId: {searchHire.ExpertId}, ClientId: {searchHire.ClientId}. " +
                                    $"Stack Trace: {ex.StackTrace}. " +
                                    $"ACTION REQUIRED: Review error details and retry if applicable.",
                            userId: userId,
                            source: "SearchHireController.CompleteService",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHire.Id,
                            additionalData: new { 
                                SearchHireId = searchHire.Id,
                                ExpertId = searchHire.ExpertId,
                                ClientId = searchHire.ClientId,
                                Amount = searchHire.Amount,
                                Status = searchHire.Status?.StatusValue,
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