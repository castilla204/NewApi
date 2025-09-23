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

        /// <summary>
        /// Constructor del controlador de disputas
        /// </summary>
        /// <param name="context">Contexto de la base de datos</param>
        /// <param name="logger">Logger para registro de eventos</param>
        /// <param name="configuration">Configuración de la aplicación</param>
        /// <param name="storageClient">Cliente de Google Cloud Storage</param>
        public DisputeController(AppDbContext context, ILogger<DisputeController> logger, IConfiguration configuration, StorageClient storageClient)
        {
            _context = context;
            _logger = logger;
            _configuration = configuration;
            _storageClient = storageClient;
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
                    .Include(d => d.Files) // ✅ NUEVO: Incluir archivos
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
                        FileUrl = f.FilePath
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

        /// <summary>
        /// Crea una nueva disputa con archivos adjuntos (usuarios normales)
        /// </summary>
        [HttpPost("dispute-service")]
        public async Task<IActionResult> CreateDisputeWithFiles([FromForm] CreateDisputeDto request)
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
                    return BadRequest(new { message = "Reason is required" });
                }

                if (request.Reason.Length > 1000)
                {
                    return BadRequest(new { message = "Reason cannot exceed 1000 characters" });
                }

                // Verificar que el SearchHire existe y pertenece al usuario
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Search)
                    .FirstOrDefaultAsync(sh => sh.Id == request.SearchHireId);

                if (searchHire == null)
                {
                    return NotFound(new { message = "Service not found" });
                }

                // Verificar que el usuario es el cliente o el experto del servicio
                if (searchHire.ClientId != userId && searchHire.ExpertId != userId)
                {
                    return Forbid("You can only dispute services you are involved in");
                }

                // Verificar que no existe ya una disputa para este SearchHire
                var existingDispute = await _context.Disputes
                    .FirstOrDefaultAsync(d => d.SearchHireId == request.SearchHireId);

                if (existingDispute != null)
                {
                    return BadRequest(new { message = "A dispute already exists for this service" });
                }

                // Verificar que el servicio está en un estado que permite disputas
                if (searchHire.Status != SearchHireStatus.Completed.ToStringValue() && searchHire.Status != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                {
                    return BadRequest(new { message = "Cannot dispute this service in its current status" });
                }

                using var transaction = await _context.Database.BeginTransactionAsync();

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

                _context.Disputes.Add(dispute);
                await _context.SaveChangesAsync();

                // Actualizar el estado del SearchHire a Disputed
                searchHire.Status = SearchHireStatus.Disputed.ToStringValue();
                await _context.SaveChangesAsync();

                // Handle file uploads if any
                if (request.Files != null && request.Files.Count > 0)
                {
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
                                await transaction.RollbackAsync();
                                return BadRequest(new { message = $"File type {fileExtension} is not allowed. Allowed types: {string.Join(", ", allowedExtensions)}" });
                            }

                            if (file.Length > 10 * 1024 * 1024) // 10MB limit
                            {
                                await transaction.RollbackAsync();
                                return BadRequest(new { message = "File size cannot exceed 10MB" });
                            }

                            var uniqueFileName = $"{Guid.NewGuid()}{fileExtension}";
                            var objectName = $"disputes/{uniqueFileName}";

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
                                    DisputeId = dispute.Id,
                                    FileName = file.FileName,
                                    FilePath = fileUrl,
                                    FileType = fileExtension,
                                    FileSize = file.Length,
                                    CreatedAt = DateTime.UtcNow
                                });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error uploading dispute file: {FileName}", file.FileName);
                                await transaction.RollbackAsync();
                                return StatusCode(500, new { message = "Failed to upload file" });
                            }
                        }
                    }

                    if (disputeFiles.Any())
                    {
                        _context.DisputeFiles.AddRange(disputeFiles);
                        await _context.SaveChangesAsync();
                    }
                }

                await transaction.CommitAsync();

                _logger.LogInformation("Dispute opened for searchHireId={SearchHireId}, disputeId={DisputeId}", searchHire.Id, dispute.Id);
                return Ok(new { message = "Dispute opened successfully", disputeId = dispute.Id });
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
                var userEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? "";

                // Verificar si es admin
                var isAdmin = userEmail == "dcastillaa@gmail.com";

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
                    .Include(d => d.Files)
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
                    },
                    Files = dispute.Files.Select(f => new DisputeFileDto
                    {
                        Id = f.Id,
                        FileName = f.FileName,
                        FileType = f.FileType,
                        FileSize = f.FileSize,
                        CreatedAt = f.CreatedAt,
                        FilePath = f.FilePath,
                        FileUrl = f.FilePath
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
                        .ThenInclude(sh => sh.Expert)
                    .Include(d => d.Files)
                    .FirstOrDefaultAsync(d => d.Id == disputeId);

                if (dispute == null)
                {
                    return NotFound(new { message = "Dispute not found" });
                }

                // Verificar que el usuario es el experto de esta disputa
                if (!dispute.SearchHire.ExpertId.HasValue || dispute.SearchHire.ExpertId.Value != userId)
                {
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

                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    // Actualizar la respuesta del experto
                    dispute.ExpertResponse = request.Response;
                    dispute.ExpertResponseAt = DateTime.UtcNow;

                    // Handle file uploads if any
                    if (request.Files != null && request.Files.Count > 0)
                    {
                        var bucketName = _configuration["GoogleCloud:BucketName"];
                        if (string.IsNullOrEmpty(bucketName))
                        {
                            await transaction.RollbackAsync();
                            return StatusCode(500, new { message = "Google Cloud Storage configuration missing" });
                        }

                        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".pdf", ".doc", ".docx", ".mp4", ".avi", ".mov" };
                        var maxFileSize = 10 * 1024 * 1024; // 10MB

                        foreach (var file in request.Files)
                        {
                            // Validar tipo de archivo
                            var fileExtension = Path.GetExtension(file.FileName).ToLowerInvariant();
                            if (!allowedExtensions.Any(ext => ext == fileExtension))
                            {
                                await transaction.RollbackAsync();
                                return BadRequest(new { message = $"File type {fileExtension} is not allowed" });
                            }

                            // Validar tamaño
                            if (file.Length > maxFileSize)
                            {
                                await transaction.RollbackAsync();
                                return BadRequest(new { message = $"File {file.FileName} exceeds maximum size of 10MB" });
                            }

                            // Generar nombre único para el archivo
                            var fileName = $"dispute-{disputeId}/expert-response/{Guid.NewGuid()}{fileExtension}";

                            // Subir archivo a Google Cloud Storage
                            using var memoryStream = new MemoryStream();
                            await file.CopyToAsync(memoryStream);
                            memoryStream.Position = 0;

                            await _storageClient.UploadObjectAsync(bucketName, fileName, null, memoryStream);

                            // Crear URL del archivo
                            var fileUrl = $"https://storage.googleapis.com/{bucketName}/{fileName}";

                            // Crear registro del archivo
                            var disputeFile = new DisputeFile
                            {
                                DisputeId = dispute.Id,
                                FileName = file.FileName,
                                FilePath = fileUrl,
                                FileType = fileExtension,
                                FileSize = file.Length,
                                CreatedAt = DateTime.UtcNow
                            };

                            _context.DisputeFiles.Add(disputeFile);
                        }
                    }

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    _logger.LogInformation("Expert {ExpertId} responded to dispute {DisputeId}", userId, disputeId);

                    return Ok(new { message = "Expert response submitted successfully" });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Error submitting expert response for dispute {DisputeId}", disputeId);
                    return StatusCode(500, new { message = "Error submitting expert response" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing expert response for dispute {DisputeId}", disputeId);
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
}