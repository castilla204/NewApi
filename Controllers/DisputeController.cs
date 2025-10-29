using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Common;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Configuration;
using System.IO;
using newApi.Services;

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
        private readonly IConfiguration _configuration;
        private readonly StorageClient _storageClient;
        private readonly IAuthorizationServices _authService;
        private readonly SystemStatusService _systemStatusService;
        private readonly StripeRefundService _refundService;

        /// <summary>
        /// Constructor del controlador de disputas
        /// </summary>
        /// <param name="context">Contexto de la base de datos</param>
        /// <param name="logger">Logger para registro de eventos</param>
        /// <param name="configuration">Configuración de la aplicación</param>
        /// <param name="storageClient">Cliente de Google Cloud Storage</param>
        public DisputeController(AppDbContext context, ILogger<DisputeController> logger, IConfiguration configuration, StorageClient storageClient, IAuthorizationServices authService, SystemStatusService systemStatusService, StripeRefundService refundService)
        {
            _context = context;
            _logger = logger;
            _configuration = configuration;
            _storageClient = storageClient;
            _authService = authService;
            _systemStatusService = systemStatusService;
            _refundService = refundService;
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

                if (searchHire.Status.StatusValue != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                {
                    _logger.LogError("Service is not in awaiting_client_decision status: searchHireId={SearchHireId}, status={Status}", searchHire.Id, searchHire.Status.StatusValue);
                    return BadRequest(new { message = "Service is not awaiting client decision" });
                }

                // Usar la estrategia de ejecución de Entity Framework para manejar transacciones
                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    try
                    {
                        searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Disputed.ToStringValue());
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
                });
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
                // 🔐 SEGURIDAD: Verificar rol en lugar de email
                if (!_authService.IsAdmin(User))
                {
                    _logger.LogError("Unauthorized access attempt to dispute endpoint by user={UserId}", User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
                    return Unauthorized(new { message = "Admin access required" });
                }

                // Validar parámetros
                if (request.Page < 1) request.Page = 1;
                if (request.PageSize < 1 || request.PageSize > 50) request.PageSize = 20;

                // Construir query base con includes
                var query = _context.Disputes
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Search)
                    .Include(d => d.Reporter)
                    .Include(d => d.Files)
                        .ThenInclude(f => f.UploadedByUser) // ✅ NUEVO: Incluir usuario que subió el archivo
                    .AsQueryable();

                // Aplicar filtros
                if (!string.IsNullOrEmpty(request.SearchTerm))
                {
                    var searchTerm = request.SearchTerm.ToLower();
                    query = query.Where(d => 
                        d.Reason.ToLower().Contains(searchTerm) || 
                        (d.ResolutionComments != null && d.ResolutionComments.ToLower().Contains(searchTerm)) ||
                        (d.ExpertResponse != null && d.ExpertResponse.ToLower().Contains(searchTerm))); // ✅ NUEVO: Buscar en respuesta del experto
                        
                        
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
                    StatusTranslated = d.Status.ToSpanishTranslation(),
                    ResolutionComments = d.ResolutionComments,
                    CreatedAt = d.CreatedAt,
                    
                    // ✅ NUEVOS CAMPOS: Respuesta del experto
                    ExpertResponse = d.ExpertResponse,
                    ExpertResponseDeadline = d.ExpertResponseDeadline,
                    ExpertResponseAt = d.ExpertResponseAt,
                    CanExpertRespond = d.CanExpertRespond,
                    
                    SearchHire = new SearchHireInfoDto
                    {
                        Id = d.SearchHire.Id,
                        Status = d.SearchHire.Status.StatusValue,
                        StatusTranslated = d.SearchHire.Status.StatusValue.ToSpanishTranslation(),
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
                    },
                    // ✅ NUEVO: Archivos adjuntos
                    Files = d.Files.Select(f => new DisputeFileDto
                    {
                        Id = f.Id,
                        FileName = f.FileName,
                        FileType = f.FileType,
                        FileSize = f.FileSize,
                        CreatedAt = f.CreatedAt,
                        FilePath = f.FilePath,
                        FileUrl = f.FilePath,
                        UploadedByUserId = f.UploadedByUserId,
                        UploadedByUserName = f.UploadedByUser?.Name ?? "Usuario desconocido",
                        UploadedByUserEmail = f.UploadedByUser?.Email ?? "",
                        FileCategory = f.FileCategory,
                        FileCategoryLabel = f.FileCategory == "client" ? "Archivo del Cliente" : "Archivo del Experto"
                    }).ToList()
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
                // 🔐 SEGURIDAD: Verificar rol en lugar de email
                if (!_authService.IsAdmin(User))
                {
                    _logger.LogError("Unauthorized access attempt to dispute endpoint by user={UserId}", User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
                    return Unauthorized(new { message = "Admin access required" });
                }

                var dispute = await _context.Disputes
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Status)
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

                // Usar la estrategia de ejecución de Entity Framework para manejar transacciones
                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
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
                                {
                                    var refundReason = $"Dispute resolved in favor of client: {request.ResolutionComments}";
                                    var refundSuccess = await _refundService.ProcessMoneyDistributionAsync(
                                        dispute.SearchHire.Id,
                                        "dispute_resolved_client",
                                        refundReason);
                                    if (!refundSuccess)
                                    {
                                        _logger.LogError("Failed to process client refund for dispute searchHireId={SearchHireId}", dispute.SearchHire.Id);
                                        await transaction.RollbackAsync();
                                        return StatusCode(500, new { message = "Failed to process client refund" });
                                    }
                                    dispute.SearchHire.StatusId = await GetStatusIdByValueAsync("dispute_resolved_client");
                                    dispute.SearchHire.UpdatedAt = DateTime.UtcNow;
                                    break;
                                }

                            case "pay_expert":
                                {
                                    var transferSuccess = await _refundService.ProcessMoneyDistributionAsync(
                                        dispute.SearchHire.Id,
                                        "dispute_resolved_expert",
                                        "Dispute resolved in favor of expert");
                                    if (!transferSuccess)
                                    {
                                        _logger.LogError("Failed to process expert transfer for dispute searchHireId={SearchHireId}", dispute.SearchHire.Id);
                                        await transaction.RollbackAsync();
                                        return StatusCode(500, new { message = "Failed to process expert transfer" });
                                    }
                                    dispute.SearchHire.StatusId = await GetStatusIdByValueAsync("dispute_resolved_expert");
                                    dispute.SearchHire.UpdatedAt = DateTime.UtcNow;
                                    break;
                                }

                            default:
                                return BadRequest(new { message = "Invalid action. Valid actions: refund_client, pay_expert" });
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
                });
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
                // 🔐 SEGURIDAD: Verificar rol en lugar de email
                if (!_authService.IsAdmin(User))
                {
                    _logger.LogError("Unauthorized access attempt to dispute endpoint by user={UserId}", User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
                    return Unauthorized(new { message = "Admin access required" });
                }

                var dispute = await _context.Disputes
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Status)
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
                        Status = dispute.SearchHire.Status.StatusValue,
                        StatusTranslated = dispute.SearchHire.Status.StatusValue.ToSpanishTranslation(),
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
                // 🔐 SEGURIDAD: Verificar rol en lugar de email
                if (!_authService.IsAdmin(User))
                {
                    _logger.LogError("Unauthorized access attempt to dispute endpoint by user={UserId}", User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
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
                        Status = dispute.SearchHire.Status.StatusValue,
                        StatusTranslated = dispute.SearchHire.Status.StatusValue.ToSpanishTranslation(),
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

        /// <summary>
        /// Crea una nueva disputa con archivos adjuntos (usuarios normales)
        /// </summary>
        [HttpPost("dispute-service")]
        public async Task<IActionResult> CreateDisputeWithFiles([FromForm] CreateDisputeDto request)
        {
            try
            {
                _logger.LogInformation("Starting dispute creation process. SearchHireId={SearchHireId}, Reason length={ReasonLength}, Files count={FilesCount}", 
                    request?.SearchHireId, request?.Reason?.Length, request?.Files?.Count);

                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    _logger.LogError("Invalid user identification: userIdClaim={UserIdClaim}", userIdClaim);
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                _logger.LogInformation("User authenticated successfully. UserId={UserId}", userId);

                if (string.IsNullOrWhiteSpace(request.Reason))
                {
                    return BadRequest(new { message = "Reason is required" });
                }

                if (request.Reason.Length > 1000)
                {
                    return BadRequest(new { message = "Reason cannot exceed 1000 characters" });
                }

                // Verificar que el SearchHire existe y pertenece al usuario
                _logger.LogInformation("Looking for SearchHire with Id={SearchHireId}", request.SearchHireId);
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Search)
                    .Include(sh => sh.Status)
                    .FirstOrDefaultAsync(sh => sh.Id == request.SearchHireId);

                if (searchHire == null)
                {
                    _logger.LogWarning("SearchHire not found for Id={SearchHireId}", request.SearchHireId);
                    return NotFound(new { message = "Service not found" });
                }

                _logger.LogInformation("SearchHire found. ClientId={ClientId}, ExpertId={ExpertId}, Status={Status}", 
                    searchHire.ClientId, searchHire.ExpertId, searchHire.Status?.StatusValue);

                // Verificar que el usuario es el cliente o el experto del servicio
                if (searchHire.ClientId != userId && searchHire.ExpertId != userId)
                {
                    return Forbid();
                }

                // Verificar que no existe ya una disputa para este SearchHire
                var existingDispute = await _context.Disputes
                    .FirstOrDefaultAsync(d => d.SearchHireId == request.SearchHireId);

                if (existingDispute != null)
                {
                    return BadRequest(new { message = "A dispute already exists for this service" });
                }

                // Verificar que el servicio está en un estado que permite disputas
                if (searchHire.Status?.StatusValue != SearchHireStatus.Completed.ToStringValue() && searchHire.Status?.StatusValue != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                {
                    return BadRequest(new { message = "Cannot dispute this service in its current status" });
                }

                _logger.LogInformation("Starting database transaction for dispute creation using execution strategy");
                
                // Variable para almacenar el ID de la disputa creada
                int disputeId = 0;
                
                // Usar la estrategia de ejecución de Entity Framework para manejar transacciones
                var strategy = _context.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    
                    try
                    {
                        // Crear la disputa
                        var dispute = new DataLayer.Models.PostGresModels.Dispute
                        {
                            SearchHireId = request.SearchHireId,
                            ReporterId = userId,
                            Reason = request.Reason,
                            Status = "Pending",
                            CreatedAt = DateTime.UtcNow,
                            // Establecer ventana de 48h para que el experto responda
                            ExpertResponseDeadline = DateTime.UtcNow.AddHours(48)
                        };

                        _logger.LogInformation("Adding dispute to context. SearchHireId={SearchHireId}, ReporterId={ReporterId}", 
                            dispute.SearchHireId, dispute.ReporterId);
                        _context.Disputes.Add(dispute);
                        await _context.SaveChangesAsync();
                        _logger.LogInformation("Dispute saved successfully. DisputeId={DisputeId}", dispute.Id);

                        // Actualizar el estado del SearchHire a Disputed
                        searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Disputed.ToStringValue());
                        await _context.SaveChangesAsync();
                        
                        await transaction.CommitAsync();
                        _logger.LogInformation("Transaction committed successfully");
                        
                        // Guardar el ID para uso posterior
                        disputeId = dispute.Id;
                    }
                    catch
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                });

                // Handle file uploads if any (outside transaction for better performance)
                if (request.Files != null && request.Files.Count > 0)
                {
                    _logger.LogInformation("Processing file uploads for dispute");
                    var bucketName = _configuration["GoogleCloud:BucketName"];
                    var disputeFiles = new List<DisputeFile>();
                    
                    foreach (var file in request.Files)
                    {
                        if (file.Length > 0)
                        {
                            // Validate file type and size
                            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".pdf", ".doc", ".docx", ".mp4", ".avi", ".mov" };
                            var fileExtension = Path.GetExtension(file.FileName).ToLowerInvariant();
                            
                            if (!allowedExtensions.Any(ext => ext == fileExtension))
                            {
                                return BadRequest(new { message = $"File type {fileExtension} is not allowed. Allowed types: {string.Join(", ", allowedExtensions)}" });
                            }

                            if (file.Length > 10 * 1024 * 1024) // 10MB limit
                            {
                                return BadRequest(new { message = "File size cannot exceed 10MB" });
                            }

                            var uniqueFileName = $"{Guid.NewGuid()}{fileExtension}";
                            var objectName = $"disputes/dispute-{disputeId}/client-files/{uniqueFileName}";

                            try
                            {
                                using (var inputStream = file.OpenReadStream())
                                {
                                    await _storageClient.UploadObjectAsync(
                                        bucket: bucketName,
                                        objectName: objectName,
                                        contentType: file.ContentType,
                                        source: inputStream
                                    );
                                }

                                var fileUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";

                                disputeFiles.Add(new DisputeFile
                                {
                                    DisputeId = disputeId,
                                    FileName = file.FileName,
                                    FilePath = fileUrl,
                                    FileType = fileExtension,
                                    FileSize = file.Length,
                                    CreatedAt = DateTime.UtcNow,
                                    UploadedByUserId = userId,
                                    FileCategory = "client" // Archivos subidos al crear la disputa son del cliente
                                });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error uploading dispute file: {FileName}", file.FileName);
                                return StatusCode(500, new { message = "Failed to upload file" });
                            }
                        }
                    }

                    if (disputeFiles.Any())
                    {
                        _context.DisputeFiles.AddRange(disputeFiles);
                        await _context.SaveChangesAsync();
                        _logger.LogInformation("Files uploaded successfully for dispute");
                    }
                }

                _logger.LogInformation("Dispute opened for searchHireId={SearchHireId}, disputeId={DisputeId}", searchHire.Id, disputeId);
                return Ok(new { message = "Dispute opened successfully", disputeId = disputeId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating dispute for searchHireId={SearchHireId}", request.SearchHireId);
                return StatusCode(500, new { message = "An error occurred while creating the dispute" });
            }
        }

        /// <summary>
        /// Obtiene las disputas del usuario actual (cliente, experto o admin)
        /// </summary>
        [HttpGet("my-disputes")]
        public async Task<IActionResult> GetMyDisputes([FromQuery] DisputeListRequestDto request)
        {
            try
            {
                var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                // 🔐 SEGURIDAD: Verificar rol en lugar de email
                var isAdmin = _authService.IsAdmin(User);

                // Validar parámetros
                if (request.Page < 1) request.Page = 1;
                if (request.PageSize < 1 || request.PageSize > 50) request.PageSize = 20;

                // Construir query base con includes
                var query = _context.Disputes
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Search)
                    .Include(d => d.Reporter)
                    .Include(d => d.Files)
                        .ThenInclude(f => f.UploadedByUser) // ✅ NUEVO: Incluir usuario que subió el archivo
                    .AsQueryable();

                // Si no es admin, filtrar solo las disputas donde el usuario participa
                if (!isAdmin)
                {
                    query = query.Where(d => 
                        d.ReporterId == userId || // Usuario reportó la disputa
                        d.SearchHire.ClientId == userId || // Usuario es el cliente
                        (d.SearchHire.ExpertId.HasValue && d.SearchHire.ExpertId.Value == userId) // Usuario es el experto
                    );
                }

                // Aplicar filtros adicionales
                if (!string.IsNullOrEmpty(request.SearchTerm))
                {
                    var searchTerm = request.SearchTerm.ToLower();
                    query = query.Where(d => 
                        d.Reason.ToLower().Contains(searchTerm) || 
                        (d.ResolutionComments != null && d.ResolutionComments.ToLower().Contains(searchTerm)) ||
                        (d.ExpertResponse != null && d.ExpertResponse.ToLower().Contains(searchTerm)));
                }

                if (!string.IsNullOrEmpty(request.Status))
                {
                    query = query.Where(d => d.Status == request.Status);
                }

                if (request.ReporterId.HasValue && isAdmin)
                {
                    query = query.Where(d => d.ReporterId == request.ReporterId.Value);
                }

                if (request.ClientId.HasValue && isAdmin)
                {
                    query = query.Where(d => d.SearchHire.ClientId == request.ClientId.Value);
                }

                if (request.ExpertId.HasValue && isAdmin)
                {
                    query = query.Where(d => d.SearchHire.ExpertId == request.ExpertId.Value);
                }

                if (request.StartDate.HasValue)
                {
                    query = query.Where(d => d.CreatedAt >= request.StartDate.Value);
                }

                if (request.EndDate.HasValue)
                {
                    query = query.Where(d => d.CreatedAt <= request.EndDate.Value);
                }

                // Aplicar ordenamiento
                query = ApplySorting(query, request.SortBy, request.SortDirection);

                // Contar total
                var totalCount = await query.CountAsync();

                // Aplicar paginación
                var disputes = await query
                    .Skip((request.Page - 1) * request.PageSize)
                    .Take(request.PageSize)
                    .ToListAsync();

                // Mapear a DTOs
                var disputeDtos = disputes.Select(dispute => new DisputeDto
                {
                    Id = dispute.Id,
                    SearchHireId = dispute.SearchHireId,
                    ReporterId = dispute.ReporterId,
                    Reason = dispute.Reason,
                    Status = dispute.Status,
                    StatusTranslated = dispute.Status.ToSpanishTranslation(),
                    ResolutionComments = dispute.ResolutionComments,
                    CreatedAt = dispute.CreatedAt,
                    ExpertResponse = dispute.ExpertResponse,
                    ExpertResponseDeadline = dispute.ExpertResponseDeadline,
                    ExpertResponseAt = dispute.ExpertResponseAt,
                    CanExpertRespond = dispute.CanExpertRespond,
                    SearchHire = new SearchHireInfoDto
                    {
                        Id = dispute.SearchHire.Id,
                        Status = dispute.SearchHire.Status.StatusValue,
                        StatusTranslated = dispute.SearchHire.Status.StatusValue.ToSpanishTranslation(),
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
                    },
                    Files = dispute.Files.Select(f => new DisputeFileDto
                    {
                        Id = f.Id,
                        FileName = f.FileName,
                        FileType = f.FileType,
                        FileSize = f.FileSize,
                        CreatedAt = f.CreatedAt,
                        FilePath = f.FilePath,
                        FileUrl = f.FilePath,
                        UploadedByUserId = f.UploadedByUserId,
                        UploadedByUserName = f.UploadedByUser?.Name ?? "Usuario desconocido",
                        UploadedByUserEmail = f.UploadedByUser?.Email ?? "",
                        FileCategory = f.FileCategory,
                        FileCategoryLabel = f.FileCategory == "client" ? "Archivo del Cliente" : "Archivo del Experto"
                    }).ToList()
                }).ToList();

                // Calcular estadísticas solo si es admin
                DisputeStats? stats = null;
                if (isAdmin)
                {
                    stats = await CalculateDisputeStats();
                }

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
                _logger.LogError(ex, "Error retrieving user disputes");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        /// <summary>
        /// DEBUG: Obtiene información de una disputa para debugging
        /// </summary>
        [HttpGet("{disputeId}/debug")]
        public async Task<IActionResult> GetDisputeDebug(int disputeId)
        {
            try
            {
                var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                var userEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? "";

                var dispute = await _context.Disputes
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .FirstOrDefaultAsync(d => d.Id == disputeId);

                if (dispute == null)
                {
                    return NotFound(new { message = "Dispute not found" });
                }

                return Ok(new
                {
                    disputeId = dispute.Id,
                    authenticatedUserId = userId,
                    authenticatedUserEmail = userEmail,
                    searchHireId = dispute.SearchHireId,
                    expertId = dispute.SearchHire.ExpertId,
                    clientId = dispute.SearchHire.ClientId,
                    reporterId = dispute.ReporterId,
                    status = dispute.Status,
                    canExpertRespond = dispute.CanExpertRespond,
                    expertResponseDeadline = dispute.ExpertResponseDeadline,
                    expertResponseAt = dispute.ExpertResponseAt,
                    isUserExpert = dispute.SearchHire.ExpertId.HasValue && dispute.SearchHire.ExpertId.Value == userId,
                    isUserClient = dispute.SearchHire.ClientId == userId,
                    isUserReporter = dispute.ReporterId == userId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in debug endpoint for dispute {DisputeId}", disputeId);
                return StatusCode(500, new { message = ex.Message });
            }
        }

        /// <summary>
        /// Permite al experto responder a una disputa del cliente
        /// </summary>
        [HttpPost("{disputeId}/expert-response")]
        public async Task<IActionResult> ExpertResponseToDispute(int disputeId, [FromForm] ExpertResponseDto request)
        {
            try
            {
                var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");

                var dispute = await _context.Disputes
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(d => d.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(d => d.Files)
                    .FirstOrDefaultAsync(d => d.Id == disputeId);

                if (dispute == null)
                {
                    return NotFound(new { message = "Dispute not found" });
                }

                // Verificar que el usuario es el experto de esta disputa
                _logger.LogInformation("Expert validation - UserId: {UserId}, ExpertId: {ExpertId}, HasExpertId: {HasExpertId}", 
                    userId, dispute.SearchHire.ExpertId, dispute.SearchHire.ExpertId.HasValue);
                
                if (!dispute.SearchHire.ExpertId.HasValue || dispute.SearchHire.ExpertId.Value != userId)
                {
                    _logger.LogWarning("Expert validation failed - UserId: {UserId}, ExpertId: {ExpertId}", 
                        userId, dispute.SearchHire.ExpertId);
                    return Forbid();
                }

                // Verificar que la disputa está pendiente
                if (dispute.Status != "Pending")
                {
                    return BadRequest(new { message = "Cannot respond to a resolved dispute" });
                }

                // Verificar que el experto puede aún responder (dentro de las 48h)
                if (!dispute.CanExpertRespond)
                {
                    return BadRequest(new { message = "The deadline to respond has expired (48 hours)" });
                }

                // Verificar que el experto no ha respondido ya
                if (dispute.ExpertResponseAt.HasValue)
                {
                    return BadRequest(new { message = "Expert has already responded to this dispute" });
                }

                _logger.LogInformation("Starting database transaction for expert response using execution strategy");
                
                // Usar la estrategia de ejecución de Entity Framework para manejar transacciones
                var strategy = _context.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    
                    try
                    {
                        // Actualizar la respuesta del experto
                        dispute.ExpertResponse = request.Response;
                        dispute.ExpertResponseAt = DateTime.UtcNow;

                        await _context.SaveChangesAsync();
                        await transaction.CommitAsync();
                        
                        _logger.LogInformation("Expert response saved successfully. DisputeId={DisputeId}", disputeId);
                    }
                    catch
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                });

                // Handle file uploads if any (outside transaction for better performance)
                if (request.Files != null && request.Files.Count > 0)
                {
                    _logger.LogInformation("Processing file uploads for expert response");
                    var bucketName = _configuration["GoogleCloud:BucketName"];
                    if (string.IsNullOrEmpty(bucketName))
                    {
                        return StatusCode(500, new { message = "Google Cloud Storage configuration missing" });
                    }

                    var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".pdf", ".doc", ".docx", ".mp4", ".avi", ".mov" };
                    var maxFileSize = 10 * 1024 * 1024; // 10MB
                    var disputeFiles = new List<DisputeFile>();

                    foreach (var file in request.Files)
                    {
                        // Validar tipo de archivo
                        var fileExtension = Path.GetExtension(file.FileName).ToLowerInvariant();
                        if (!allowedExtensions.Any(ext => ext == fileExtension))
                        {
                            return BadRequest(new { message = $"File type {fileExtension} is not allowed" });
                        }

                        // Validar tamaño
                        if (file.Length > maxFileSize)
                        {
                            return BadRequest(new { message = $"File {file.FileName} exceeds maximum size of 10MB" });
                        }

                        // Generar nombre único para el archivo
                        var fileName = $"disputes/dispute-{disputeId}/expert-files/{Guid.NewGuid()}{fileExtension}";

                        try
                        {
                            // Subir archivo a Google Cloud Storage
                            using var memoryStream = new MemoryStream();
                            await file.CopyToAsync(memoryStream);
                            memoryStream.Position = 0;

                            await _storageClient.UploadObjectAsync(bucketName, fileName, null, memoryStream);

                            // Crear URL del archivo
                            var fileUrl = $"https://storage.googleapis.com/{bucketName}/{fileName}";

                            // Crear registro del archivo
                            disputeFiles.Add(new DisputeFile
                            {
                                DisputeId = disputeId,
                                FileName = file.FileName,
                                FilePath = fileUrl,
                                FileType = fileExtension,
                                FileSize = file.Length,
                                CreatedAt = DateTime.UtcNow,
                                UploadedByUserId = userId,
                                FileCategory = "expert" // Archivos subidos en la respuesta del experto son del experto
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error uploading expert response file: {FileName}", file.FileName);
                            return StatusCode(500, new { message = "Failed to upload file" });
                        }
                    }

                    if (disputeFiles.Any())
                    {
                        _context.DisputeFiles.AddRange(disputeFiles);
                        await _context.SaveChangesAsync();
                        _logger.LogInformation("Files uploaded successfully for expert response");
                    }
                }

                _logger.LogInformation("Expert {ExpertId} responded to dispute {DisputeId}", userId, disputeId);
                return Ok(new { message = "Expert response submitted successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing expert response for dispute {DisputeId}", disputeId);
                return StatusCode(500, new { message = ex.Message });
            }
        }

    }
}