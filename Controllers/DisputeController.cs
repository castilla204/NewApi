using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Common;

namespace newApi.Controllers
{
    /// <summary>
    /// Controlador para la gestión de disputas por parte de administradores
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class DisputeController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<DisputeController> _logger;

        /// <summary>
        /// Constructor del controlador de disputas
        /// </summary>
        /// <param name="context">Contexto de la base de datos</param>
        /// <param name="logger">Logger para registro de eventos</param>
        public DisputeController(AppDbContext context, ILogger<DisputeController> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// Crea una nueva disputa (usuarios normales)
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CreateDispute([FromBody] CreateDisputeDto request)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                if (string.IsNullOrWhiteSpace(request.Reason))
                {
                    _logger.LogError("Dispute reason is required for searchHireId={SearchHireId}", request.SearchHireId);
                    return BadRequest(new { message = "Dispute reason is required" });
                }

                var searchHire = await _context.SearchHires
                    .FirstOrDefaultAsync(sh => sh.Id == request.SearchHireId && sh.ClientId == userId);

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found or user is not the client for searchHireId={SearchHireId}, userId={UserId}", request.SearchHireId, userId);
                    return NotFound(new { message = "Service not found or unauthorized" });
                }

                if (searchHire.Status != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                {
                    _logger.LogError("Service is not in awaiting_client_decision status: searchHireId={SearchHireId}, status={Status}", searchHire.Id, searchHire.Status);
                    return BadRequest(new { message = "Service is not awaiting client decision" });
                }

                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    searchHire.Status = SearchHireStatus.Disputed.ToStringValue();
                    searchHire.ClientApproved = false;
                    searchHire.UpdatedAt = DateTime.UtcNow;

                    var dispute = new Dispute
                    {
                        SearchHireId = searchHire.Id,
                        ReporterId = userId,
                        Reason = request.Reason,
                        Status = "Pending",
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.Disputes.Add(dispute);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    _logger.LogInformation("Dispute opened for searchHireId={SearchHireId}, disputeId={DisputeId}", searchHire.Id, dispute.Id);
                    return Ok(new { message = "Dispute opened", disputeId = dispute.Id });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Database error disputing service for searchHireId={SearchHireId}", searchHire.Id);
                    return StatusCode(500, new { message = "Failed to open dispute" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disputing service: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { message = "Failed to open dispute" });
            }
        }

        /// <summary>
        /// Obtiene todas las disputas con paginación y filtros (solo admin)
        /// </summary>
        [HttpGet("all")]
        public async Task<IActionResult> GetAllDisputes([FromQuery] DisputeListRequestDto request)
        {
            try
            {
                // Verificar que sea admin
                var adminEmail = User.FindFirst(ClaimTypes.Email)?.Value;
                if (adminEmail != "dcastillaa@gmail.com")
                {
                    _logger.LogError("Unauthorized access attempt to dispute endpoint by email={Email}", adminEmail);
                    return Unauthorized(new { message = "Admin access required" });
                }

                // Validar parámetros
                if (request.Page < 1) request.Page = 1;
                if (request.PageSize < 1 || request.PageSize > 50) request.PageSize = 20;

                // Construir query base con includes
                var query = _context.Disputes
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Search)
                    .Include(d => d.Reporter)
                    .AsQueryable();

                // Aplicar filtros
                if (!string.IsNullOrEmpty(request.SearchTerm))
                {
                    var searchTerm = request.SearchTerm.ToLower();
                    query = query.Where(d => 
                        d.Reason.ToLower().Contains(searchTerm) || 
                        (d.ResolutionComments != null && d.ResolutionComments.ToLower().Contains(searchTerm)));
                }

                if (!string.IsNullOrEmpty(request.Status))
                {
                    query = query.Where(d => d.Status == request.Status);
                }

                if (request.ReporterId.HasValue)
                {
                    query = query.Where(d => d.ReporterId == request.ReporterId.Value);
                }

                if (request.ClientId.HasValue)
                {
                    query = query.Where(d => d.SearchHire.ClientId == request.ClientId.Value);
                }

                if (request.ExpertId.HasValue)
                {
                    query = query.Where(d => d.SearchHire.ExpertId == request.ExpertId.Value);
                }

                if (request.StartDate.HasValue)
                {
                    query = query.Where(d => d.CreatedAt >= request.StartDate.Value.ToUniversalTime());
                }

                if (request.EndDate.HasValue)
                {
                    query = query.Where(d => d.CreatedAt <= request.EndDate.Value.ToUniversalTime());
                }

                // Contar total de resultados
                var totalCount = await query.CountAsync();

                // Aplicar ordenamiento
                query = ApplySorting(query, request.SortBy, request.SortDirection);

                // Aplicar paginación
                var disputes = await query
                    .Skip((request.Page - 1) * request.PageSize)
                    .Take(request.PageSize)
                    .ToListAsync();

                // Mapear a DTOs
                var disputeDtos = disputes.Select(d => new DisputeDto
                {
                    Id = d.Id,
                    SearchHireId = d.SearchHireId,
                    ReporterId = d.ReporterId,
                    Reason = d.Reason,
                    Status = d.Status,
                    StatusTranslated = d.Status.ToSpanishTranslation(), // ✅ NUEVO: Estado traducido al español
                    ResolutionComments = d.ResolutionComments,
                    CreatedAt = d.CreatedAt,
                    SearchHire = new SearchHireInfoDto
                    {
                        Id = d.SearchHire.Id,
                        Status = d.SearchHire.Status,
                        StatusTranslated = d.SearchHire.Status.ToSpanishTranslation(),
                        Amount = d.SearchHire.Amount,
                        CreatedAt = d.SearchHire.CreatedAt
                    },
                    Reporter = new UserDto
                    {
                        Id = d.Reporter.Id,
                        Name = d.Reporter.Name,
                        Email = d.Reporter.Email
                    },
                    Client = new UserDto
                    {
                        Id = d.SearchHire.Client.Id,
                        Name = d.SearchHire.Client.Name,
                        Email = d.SearchHire.Client.Email
                    },
                    Expert = d.SearchHire.Expert != null ? new UserDto
                    {
                        Id = d.SearchHire.Expert.Id,
                        Name = d.SearchHire.Expert.Name,
                        Email = d.SearchHire.Expert.Email
                    } : null,
                    Search = new SearchInfoDto
                    {
                        Id = d.SearchHire.Search.Id,
                        Title = d.SearchHire.Search.Title,
                        Description = d.SearchHire.Search.Description ?? "",
                        CreatedAt = d.SearchHire.Search.CreatedAt
                    }
                }).ToList();

                // Calcular estadísticas
                var stats = await CalculateDisputeStats();

                // Crear respuesta paginada
                var response = new DisputeListResponseDto
                {
                    Disputes = disputeDtos,
                    Pagination = new PaginationMetadata
                    {
                        CurrentPage = request.Page,
                        PageSize = request.PageSize,
                        TotalCount = totalCount,
                        TotalPages = (int)Math.Ceiling((double)totalCount / request.PageSize),
                        HasPrevious = request.Page > 1,
                        HasNext = request.Page < (int)Math.Ceiling((double)totalCount / request.PageSize)
                    },
                    Stats = stats
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving all disputes");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        /// <summary>
        /// Resuelve una disputa (solo admin)
        /// </summary>
        [HttpPut("{disputeId}/resolve")]
        public async Task<IActionResult> ResolveDispute(int disputeId, [FromBody] ResolveDisputeDto request)
        {
            try
            {
                // Verificar que sea admin
                var adminEmail = User.FindFirst(ClaimTypes.Email)?.Value;
                if (adminEmail != "dcastillaa@gmail.com")
                {
                    _logger.LogError("Unauthorized access attempt to dispute endpoint by email={Email}", adminEmail);
                    return Unauthorized(new { message = "Admin access required" });
                }

                var dispute = await _context.Disputes
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .FirstOrDefaultAsync(d => d.Id == disputeId);

                if (dispute == null)
                {
                    return NotFound(new { message = "Dispute not found" });
                }

                if (dispute.Status != "Pending")
                {
                    return BadRequest(new { message = "Dispute is already resolved" });
                }

                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    // Actualizar la disputa
                    dispute.Status = "Resolved";
                    dispute.ResolutionComments = request.ResolutionComments;

                    // Procesar la acción según el tipo
                    switch (request.Action.ToLower())
                    {
                        case "refund_client":
                            // Reembolsar al cliente
                            dispute.SearchHire.Client.Balance += dispute.SearchHire.Amount;
                            
                            // Crear transacción financiera
                            _context.FinancialTransactions.Add(new FinancialTransaction
                            {
                                UserId = dispute.SearchHire.ClientId,
                                Amount = dispute.SearchHire.Amount,
                                TransactionType = "DisputeRefund",
                                RelatedEntityType = "Dispute",
                                RelatedEntityId = dispute.Id,
                                CreatedAt = DateTime.UtcNow
                            });

                            // Actualizar estado del SearchHire
                            dispute.SearchHire.Status = "dispute-resolved";
                            dispute.SearchHire.UpdatedAt = DateTime.UtcNow;
                            break;

                        case "pay_expert":
                            // Pagar al experto (si existe)
                            if (dispute.SearchHire.Expert != null)
                            {
                                dispute.SearchHire.Expert.Balance += dispute.SearchHire.Amount;
                                
                                // Crear transacción financiera
                                _context.FinancialTransactions.Add(new FinancialTransaction
                                {
                                    UserId = dispute.SearchHire.ExpertId.Value,
                                    Amount = dispute.SearchHire.Amount,
                                    TransactionType = "DisputePayout",
                                    RelatedEntityType = "Dispute",
                                    RelatedEntityId = dispute.Id,
                                    CreatedAt = DateTime.UtcNow
                                });
                            }

                            // Actualizar estado del SearchHire
                            dispute.SearchHire.Status = "dispute-resolved";
                            dispute.SearchHire.UpdatedAt = DateTime.UtcNow;
                            break;

                        case "no_action":
                            // No hacer nada financiero, solo marcar como resuelto
                            dispute.SearchHire.Status = "dispute-resolved";
                            dispute.SearchHire.UpdatedAt = DateTime.UtcNow;
                            break;

                        default:
                            return BadRequest(new { message = "Invalid action. Valid actions: refund_client, pay_expert, no_action" });
                    }

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    _logger.LogInformation("Dispute {DisputeId} resolved by admin with action {Action}", disputeId, request.Action);

                    return Ok(new { message = "Dispute resolved successfully" });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Error resolving dispute {DisputeId}", disputeId);
                    return StatusCode(500, new { message = "Error resolving dispute" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving dispute {DisputeId}", disputeId);
                return StatusCode(500, new { message = ex.Message });
            }
        }

        /// <summary>
        /// Obtiene los detalles de una disputa específica (solo admin)
        /// </summary>
        [HttpGet("{disputeId}")]
        public async Task<IActionResult> GetDispute(int disputeId)
        {
            try
            {
                // Verificar que sea admin
                var adminEmail = User.FindFirst(ClaimTypes.Email)?.Value;
                if (adminEmail != "dcastillaa@gmail.com")
                {
                    _logger.LogError("Unauthorized access attempt to dispute endpoint by email={Email}", adminEmail);
                    return Unauthorized(new { message = "Admin access required" });
                }

                var dispute = await _context.Disputes
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Search)
                    .Include(d => d.Reporter)
                    .FirstOrDefaultAsync(d => d.Id == disputeId);

                if (dispute == null)
                {
                    return NotFound(new { message = "Dispute not found" });
                }

                var disputeDto = new DisputeDto
                {
                    Id = dispute.Id,
                    SearchHireId = dispute.SearchHireId,
                    ReporterId = dispute.ReporterId,
                    Reason = dispute.Reason,
                    Status = dispute.Status,
                    StatusTranslated = dispute.Status.ToSpanishTranslation(),
                    ResolutionComments = dispute.ResolutionComments,
                    CreatedAt = dispute.CreatedAt,
                    SearchHire = new SearchHireInfoDto
                    {
                        Id = dispute.SearchHire.Id,
                        Status = dispute.SearchHire.Status,
                        StatusTranslated = dispute.SearchHire.Status.ToSpanishTranslation(),
                        Amount = dispute.SearchHire.Amount,
                        CreatedAt = dispute.SearchHire.CreatedAt
                    },
                    Reporter = new UserDto
                    {
                        Id = dispute.Reporter.Id,
                        Name = dispute.Reporter.Name,
                        Email = dispute.Reporter.Email
                    },
                    Client = new UserDto
                    {
                        Id = dispute.SearchHire.Client.Id,
                        Name = dispute.SearchHire.Client.Name,
                        Email = dispute.SearchHire.Client.Email
                    },
                    Expert = dispute.SearchHire.Expert != null ? new UserDto
                    {
                        Id = dispute.SearchHire.Expert.Id,
                        Name = dispute.SearchHire.Expert.Name,
                        Email = dispute.SearchHire.Expert.Email
                    } : null,
                    Search = new SearchInfoDto
                    {
                        Id = dispute.SearchHire.Search.Id,
                        Title = dispute.SearchHire.Search.Title,
                        Description = dispute.SearchHire.Search.Description ?? "",
                        CreatedAt = dispute.SearchHire.Search.CreatedAt
                    }
                };

                return Ok(disputeDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving dispute {DisputeId}", disputeId);
                return StatusCode(500, new { message = ex.Message });
            }
        }

        /// <summary>
        /// Aplica ordenamiento a la query según los parámetros especificados
        /// </summary>
        private static IQueryable<Dispute> ApplySorting(IQueryable<Dispute> query, string? sortBy, string? sortDirection)
        {
            var isDescending = sortDirection?.ToLower() == "desc";

            return sortBy?.ToLower() switch
            {
                "reason" => isDescending ? query.OrderByDescending(d => d.Reason) : query.OrderBy(d => d.Reason),
                "status" => isDescending ? query.OrderByDescending(d => d.Status) : query.OrderBy(d => d.Status),
                "reporterid" => isDescending ? query.OrderByDescending(d => d.ReporterId) : query.OrderBy(d => d.ReporterId),
                "searchhireid" => isDescending ? query.OrderByDescending(d => d.SearchHireId) : query.OrderBy(d => d.SearchHireId),
                _ => isDescending ? query.OrderByDescending(d => d.CreatedAt) : query.OrderBy(d => d.CreatedAt) // Default: CreatedAt
            };
        }

        /// <summary>
        /// Obtiene la búsqueda completa asociada a una disputa (solo admin)
        /// </summary>
        [HttpGet("{disputeId}/search")]
        public async Task<IActionResult> GetSearchFromDispute(int disputeId)
        {
            try
            {
                // Verificar que sea admin
                var adminEmail = User.FindFirst(ClaimTypes.Email)?.Value;
                if (adminEmail != "dcastillaa@gmail.com")
                {
                    _logger.LogError("Unauthorized access attempt to dispute endpoint by email={Email}", adminEmail);
                    return Unauthorized(new { message = "Admin access required" });
                }

                var dispute = await _context.Disputes
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Search)
                            .ThenInclude(s => s.User)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Search)
                            .ThenInclude(s => s.SearchParameters)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.SearchService)
                            .ThenInclude(ss => ss.ServiceType)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.SearchService)
                            .ThenInclude(ss => ss.Images)
                    .FirstOrDefaultAsync(d => d.Id == disputeId);

                if (dispute == null)
                {
                    return NotFound(new { message = "Dispute not found" });
                }

                var search = dispute.SearchHire.Search;

                // Mapear a DTO de búsqueda completa
                var searchDto = new SearchListDto
                {
                    Id = search.Id,
                    UserId = search.UserId,
                    Frequency = search.Frequency,
                    Title = search.Title,
                    Description = search.Description ?? "",
                    IsActive = search.IsActive,
                    LastExecution = search.LastExecution,
                    NextExecution = search.NextExecution,
                    IsRevised = search.IsRevised,
                    CreatedAt = search.CreatedAt,
                    StartDate = search.StartDate,
                    User = new UserDto
                    {
                        Id = search.User.Id,
                        Name = search.User.Name,
                        Email = search.User.Email
                    },
                    SearchHire = new SearchHireDto
                    {
                        Id = dispute.SearchHire.Id,
                        ExpertId = dispute.SearchHire.ExpertId,
                        Status = dispute.SearchHire.Status,
                        StatusTranslated = dispute.SearchHire.Status.ToSpanishTranslation(),
                        CreatedAt = dispute.SearchHire.CreatedAt,
                        Expert = dispute.SearchHire.Expert != null ? new UserDto
                        {
                            Id = dispute.SearchHire.Expert.Id,
                            Name = dispute.SearchHire.Expert.Name,
                            Email = dispute.SearchHire.Expert.Email
                        } : null,
                        Service = new ServiceInfo
                        {
                            Id = dispute.SearchHire.SearchService.Id,
                            ServiceTypeName = dispute.SearchHire.SearchService.ServiceType?.Name ?? "",
                            Price = dispute.SearchHire.SearchService.Price
                        }
                    }
                };

                return Ok(searchDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving search from dispute {DisputeId}", disputeId);
                return StatusCode(500, new { message = ex.Message });
            }
        }

        /// <summary>
        /// Calcula estadísticas de disputas
        /// </summary>
        private async Task<DisputeStats> CalculateDisputeStats()
        {
            var now = DateTime.UtcNow;
            var startOfWeek = now.AddDays(-(int)now.DayOfWeek).ToUniversalTime();
            var startOfMonth = new DateTime(now.Year, now.Month, 1).ToUniversalTime();

            var pendingDisputes = await _context.Disputes.CountAsync(d => d.Status == "Pending");
            var resolvedDisputes = await _context.Disputes.CountAsync(d => d.Status == "Resolved");
            var clientDisputes = await _context.Disputes.CountAsync(d => d.SearchHire.ClientId == d.ReporterId);
            var expertDisputes = await _context.Disputes.CountAsync(d => d.SearchHire.ExpertId == d.ReporterId);
            var thisWeekDisputes = await _context.Disputes.CountAsync(d => d.CreatedAt >= startOfWeek);
            var thisMonthDisputes = await _context.Disputes.CountAsync(d => d.CreatedAt >= startOfMonth);

            return new DisputeStats
            {
                PendingDisputes = pendingDisputes,
                ResolvedDisputes = resolvedDisputes,
                ClientDisputes = clientDisputes,
                ExpertDisputes = expertDisputes,
                ThisWeekDisputes = thisWeekDisputes,
                ThisMonthDisputes = thisMonthDisputes
            };
        }
    }
}