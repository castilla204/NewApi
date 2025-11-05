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
        private readonly ILogger<SearchHireController> _logger;
        private readonly IConfiguration _configuration;
        private readonly IAuthorizationServices _authService;
        private readonly StripeRefundService _refundService;
        private readonly ILoggingService _loggingService;

        public SearchHireController(
            SearchHireService searchHireService,
            AppDbContext context,
            ILogger<SearchHireController> logger,
            IConfiguration configuration,
            IAuthorizationServices authService,
            StripeRefundService refundService,
            ILoggingService loggingService)
        {
            _searchHireService = searchHireService;
            _context = context;
            _logger = logger;
            _configuration = configuration;
            _authService = authService;
            _refundService = refundService;
            _loggingService = loggingService;
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
                _logger.LogWarning("SystemStatus not found for StatusValue: {StatusValue}", statusValue);
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

                // Programar automáticamente la verificación de respuesta del experto para 24 horas después
                var scheduledTime = searchHire.CreatedAt.AddHours(24);
                BackgroundJob.Schedule(
                    () => CheckExpertResponseAsync(searchHire.Id),
                    scheduledTime - DateTime.UtcNow
                );

                _logger.LogInformation("SearchHire created and expert response check scheduled for searchHireId={SearchHireId}, scheduledTime={ScheduledTime}", 
                    searchHire.Id, scheduledTime);

                return CreatedAtAction(nameof(GetSearchHire), new { id = searchHire.Id }, searchHire);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating search hire");
                return StatusCode(500, new { message = "Failed to create search hire" });
            }
        }

        /// <summary>
        /// Verifica si el experto ha respondido en las primeras 24 horas (método para Hangfire)
        /// </summary>
        /// <param name="searchHireId">ID del servicio contratado</param>
        public async Task CheckExpertResponseAsync(int searchHireId)
        {
            _logger.LogInformation("Checking expert response for searchHireId={SearchHireId}", searchHireId);

            try
            {
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId);

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", searchHireId);
                    return;
                }

                // Verificar que el servicio esté activo
                if (searchHire.Status.StatusValue != "active")
                {
                    _logger.LogInformation("SearchHire is not active for searchHireId={SearchHireId}, current status={Status}", 
                        searchHireId, searchHire.Status.StatusValue);
                    return;
                }

                // Calcular si han pasado 24 horas desde la contratación
                var timeSinceHire = DateTime.UtcNow - searchHire.CreatedAt;
                if (timeSinceHire.TotalHours < 24)
                {
                    _logger.LogInformation("Less than 24 hours have passed for searchHireId={SearchHireId}, hours={Hours}", 
                        searchHireId, timeSinceHire.TotalHours);
                    return;
                }

                // Verificar si el experto ha enviado algún mensaje
                var hasExpertMessage = await _context.Messages
                    .AnyAsync(m => m.Conversation.SearchHireId == searchHireId && 
                                   m.SenderId == searchHire.ExpertId && 
                                   m.SentAt > searchHire.CreatedAt);

                if (!hasExpertMessage)
                {
                    _logger.LogWarning("Expert has not responded within 24 hours for searchHireId={SearchHireId}, processing automatic refund", searchHireId);
                    
                    // Orquestar distribución de dinero por estado final 'cancelled'
                    var refundReason = "Expert did not respond within 24 hours - automatic refund";
                    var refundSuccess = await _refundService.ProcessMoneyDistributionAsync(
                        searchHireId,
                        "cancelled",
                        refundReason);
                    if (!refundSuccess)
                    {
                        _logger.LogError("Failed to process Stripe refund for automatic refund searchHireId={SearchHireId}", searchHireId);
                        
                        // 🚨 LOG CRÍTICO: Fallo en refund automático
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Automatic refund failed",
                            details: $"Automatic refund failed for SearchHire {searchHireId} - expert did not respond within 24h",
                            userId: searchHire.ClientId,
                            source: "SearchHireController.ProcessNoResponseRefund",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Action = "ProcessNoResponseRefund",
                                SearchHireId = searchHireId,
                                ClientId = searchHire.ClientId,
                                ExpertId = searchHire.ExpertId,
                                Amount = searchHire.Amount,
                                Status = "cancelled",
                                Reason = refundReason,
                                Success = false
                            }
                        );
                        return;
                    }

                    // Actualizar estado del servicio
                    searchHire.StatusId = await GetStatusIdByValueAsync("cancelled");
                    searchHire.UpdatedAt = DateTime.UtcNow;
                    
                    await _context.SaveChangesAsync();

                    _logger.LogInformation("Real Stripe automatic refund processed successfully for searchHireId={SearchHireId}, reason={Reason}", 
                        searchHireId, refundReason);
                    
                    // 🚨 LOG CRÍTICO: Refund automático exitoso
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Automatic refund processed successfully",
                        details: $"Automatic refund processed successfully for SearchHire {searchHireId} - expert did not respond within 24h",
                        userId: searchHire.ClientId,
                        source: "SearchHireController.ProcessNoResponseRefund",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { 
                            Action = "ProcessNoResponseRefund",
                            SearchHireId = searchHireId,
                            ClientId = searchHire.ClientId,
                            ExpertId = searchHire.ExpertId,
                            Amount = searchHire.Amount,
                            Status = "cancelled",
                            Reason = refundReason,
                            Success = true
                        }
                    );
                }
                else
                {
                    _logger.LogInformation("Expert has responded for searchHireId={SearchHireId}, no action needed", searchHireId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CheckExpertResponseAsync for searchHireId={SearchHireId}: {ErrorMessage}", 
                    searchHireId, ex.Message);
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
                _logger.LogError(ex, "Error retrieving search hire");
                return StatusCode(500, new { message = "Failed to retrieve search hire" });
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
                _logger.LogError(ex, "Error retrieving client hires");
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
                _logger.LogError(ex, "Error retrieving expert hires");
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
                _logger.LogError(ex, "Error updating hire status");
                return StatusCode(500, new { message = "Failed to update status" });
            }
        }

        [HttpPost("complete-service")]
        public async Task<IActionResult> CompleteService([FromBody] CompleteServiceDto request)
        {
            _logger.LogInformation("CompleteService endpoint invoked for searchHireId={SearchHireId}", request.SearchHireId);

            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
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
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", request.SearchHireId);
                    return NotFound(new { message = "Service not found" });
                }

                if (searchHire.ClientId != userId)
                {
                    _logger.LogError("User is not the client for searchHireId={SearchHireId}, userId={UserId}", searchHire.Id, userId);
                    return Unauthorized(new { message = "Unauthorized to complete this service" });
                }

                if (searchHire.Status.StatusValue != SearchHireStatus.Pending.ToStringValue() && 
                    searchHire.Status.StatusValue != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                {
                    _logger.LogError("Service cannot be approved in status: searchHireId={SearchHireId}, status={Status}", searchHire.Id, searchHire.Status.StatusValue);
                    return BadRequest(new { message = "Service cannot be approved in current state" });
                }

                if (request.ClientApproved == null)
                {
                    _logger.LogError("ClientApproved is required for client action: searchHireId={SearchHireId}", searchHire.Id);
                    return BadRequest(new { error = "ClientApproved is required" });
                }

                // 🔄 USAR EXECUTION STRATEGY para compatibilidad con NpgsqlRetryingExecutionStrategy
                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    try
                    {
                        searchHire.ClientApproved = request.ClientApproved.Value;
                        if (!searchHire.ClientApproved.Value)
                        {
                            // 🛡️ DISPUTA: Cliente rechaza servicio → Abrir disputa para revisión admin
                            var disputedStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Disputed.ToStringValue());
                            searchHire.StatusId = disputedStatusId;
                            searchHire.UpdatedAt = DateTime.UtcNow;
                            _logger.LogInformation("Client opened dispute for searchHireId={SearchHireId}", searchHire.Id);
                        }
                        else
                        {
                            var ok = await _refundService.ProcessMoneyDistributionAsync(
                                searchHire.Id,
                                SearchHireStatus.Completed.ToStringValue(),
                                "Client approved service",
                                userId);
                            if (!ok)
                            {
                                await transaction.RollbackAsync();
                                _logger.LogError("Money distribution failed for searchHireId={SearchHireId}", searchHire.Id);
                                
                                // ✅ MEJORA: NO duplicar log crítico - ProcessMoneyDistributionAsync ya lo registró
                                // 🔍 Buscar el último log crítico relacionado DESPUÉS del rollback
                                // IMPORTANTE: El log se crea ANTES de la transacción del controller
                                // Limpiar el change tracker para forzar lectura fresca de la BD
                                _context.ChangeTracker.Clear();
                                
                                // Usar FromSqlRaw con AsNoTracking para leer directamente de BD sin cache
                                var lastCriticalLog = await _context.Logs
                                    .FromSqlRaw("SELECT * FROM \"Logs\" WHERE \"RelatedEntityType\" = {0} " +
                                               "AND \"RelatedEntityId\" = {1} " +
                                               "AND \"Source\" = {2} " +
                                               "AND \"CreatedAt\" >= NOW() - INTERVAL '5 minutes' " +
                                               "ORDER BY \"CreatedAt\" DESC LIMIT 1",
                                               "SearchHire", searchHire.Id, "StripeRefundService.ProcessMoneyDistributionAsync")
                                    .AsNoTracking()
                                    .FirstOrDefaultAsync();
                                
                                if (lastCriticalLog != null)
                                {
                                    _logger.LogWarning("Money distribution failed. See critical log ID {LogId} for details: {Message}", 
                                        lastCriticalLog.Id, lastCriticalLog.Message);
                                }
                                else
                                {
                                    // Si no encontramos el log, puede ser un problema de timing o el log no se creó
                                    // En este caso, logueamos un warning pero no duplicamos el log crítico
                                    _logger.LogWarning("Money distribution failed for searchHireId={SearchHireId}. " +
                                        "ProcessMoneyDistributionAsync should have logged the error. " +
                                        "Check logs table for recent entries from StripeRefundService.ProcessMoneyDistributionAsync.", 
                                        searchHire.Id);
                                }
                                
                                return StatusCode(500, new { 
                                    message = "Failed to process payment to expert",
                                    logId = lastCriticalLog?.Id,
                                    errorMessage = lastCriticalLog?.Message,
                                    searchHireId = searchHire.Id
                                });
                            }

                            var completedStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Completed.ToStringValue());
                            searchHire.StatusId = completedStatusId;
                            searchHire.UpdatedAt = DateTime.UtcNow;
                            _logger.LogInformation("Client approved service, distribution completed for searchHireId={SearchHireId}", searchHire.Id);
                        }

                        await _context.SaveChangesAsync();
                        await transaction.CommitAsync();

                        return Ok(new { message = searchHire.ClientApproved.Value ? "Service completed" : "Dispute opened" });
                    }
                    catch (StripeException ex)
                    {
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Stripe error processing transfer for searchHireId={SearchHireId}: {ErrorMessage}", searchHire.Id, ex.Message);
                        
                        // 🚨 LOG CRÍTICO: Error de Stripe durante completado de servicio (una sola vez, antes de ProcessMoneyDistributionAsync)
                        // Este error ocurre ANTES de llamar a ProcessMoneyDistributionAsync, por lo que debe loguearse aquí
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
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Database error completing service for searchHireId={SearchHireId}", searchHire.Id);
                        
                        // 🚨 LOG CRÍTICO: Error general durante completado de servicio (una sola vez, con información completa)
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
                _logger.LogError(ex, "Error completing service: {ErrorMessage}", ex.Message);
                
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