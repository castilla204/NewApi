using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.enums;
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
        private readonly IConfiguration _configuration;
        private readonly StorageClient _storageClient;
        private readonly IAuthorizationServices _authService;
        private readonly SystemStatusService _systemStatusService;
        private readonly StripeRefundService _refundService;
        private readonly ILoggingService _loggingService;
        private readonly ISignedUrlService _signedUrlService;

        /// <summary>
        /// Constructor del controlador de disputas
        /// </summary>
        /// <param name="context">Contexto de la base de datos</param>
        /// <param name="logger">Logger para registro de eventos</param>
        /// <param name="configuration">Configuración de la aplicación</param>
        /// <param name="storageClient">Cliente de Google Cloud Storage</param>
        public DisputeController(
            AppDbContext context,
            IConfiguration configuration,
            StorageClient storageClient,
            IAuthorizationServices authService,
            SystemStatusService systemStatusService,
            StripeRefundService refundService,
            ILoggingService loggingService,
            ISignedUrlService signedUrlService)
        {
            _context = context;
            _configuration = configuration;
            _storageClient = storageClient;
            _authService = authService;
            _systemStatusService = systemStatusService;
            _refundService = refundService;
            _loggingService = loggingService;
            _signedUrlService = signedUrlService;
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
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                if (string.IsNullOrWhiteSpace(request.Reason))
                {
                    return BadRequest(new { message = "Dispute reason is required" });
                }

                var searchHire = await _context.SearchHires
                    .FirstOrDefaultAsync(sh => sh.Id == request.SearchHireId && sh.ClientId == userId);

                if (searchHire == null)
                {
                    return NotFound(new { message = "Service not found or unauthorized" });
                }

                if (searchHire.Status.StatusValue != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                {
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
                        return Ok(new { message = "Dispute opened", disputeId = dispute.Id });
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        return StatusCode(500, new { message = "Failed to open dispute" });
                    }
                });
            }
            catch (Exception ex)
            {
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
                    StatusTranslated = DisputeStatusExtensions.ToSpanishTranslation(d.Status),
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
                        StatusTranslated = SearchHireStatusExtensions.ToSpanishTranslation(d.SearchHire.Status.StatusValue),
                        Amount = d.SearchHire.Amount,
                        CreatedAt = d.SearchHire.CreatedAt
                    },
                    Reporter = new UserDto
                    {
                        Id = d.Reporter.Id,
                        Name = d.Reporter.Name,
                        Email = d.Reporter.Email
                    },
                    Client = d.SearchHire.ClientId.HasValue && d.SearchHire.Client != null ? new UserDto
                    {
                        Id = d.SearchHire.Client.Id,
                        Name = d.SearchHire.Client.Name,
                        Email = d.SearchHire.Client.Email
                    } : null, // ✅ Manejar caso donde ClientId es null (usuario eliminado)
                    Expert = d.SearchHire.ExpertId.HasValue && d.SearchHire.Expert != null ? new UserDto
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
                      Files = d.Files.Select(f =>
                      {
                          var signedFileUrl = ResolveDisputeFileUrl(f);
                          return new DisputeFileDto
                          {
                              Id = f.Id,
                              FileName = f.FileName,
                              FileType = f.FileType,
                              FileSize = f.FileSize,
                              CreatedAt = f.CreatedAt,
                              FilePath = signedFileUrl,
                              FileUrl = signedFileUrl,
                              UploadedByUserId = f.UploadedByUserId,
                              UploadedByUserName = f.UploadedByUser?.Name ?? "Usuario desconocido",
                              UploadedByUserEmail = f.UploadedByUser?.Email ?? "",
                              FileCategory = f.FileCategory,
                              FileCategoryLabel = f.FileCategory == "client" ? "Archivo del Cliente" : "Archivo del Experto"
                          };
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
                    try
                    {
                        dispute.Status = "Resolved";
                        dispute.ResolutionComments = request.ResolutionComments;
                        await _context.SaveChangesAsync();

                        switch (request.Action.ToLower())
                        {
                            case "refund_client":
                                {
                                    try
                                    {
                                        var refundReason = $"Dispute resolved in favor of client: {request.ResolutionComments}";
                                        var refundSuccess = await _refundService.ProcessMoneyDistributionAsync(
                                            dispute.SearchHire.Id,
                                            SearchHireStatus.DisputeResolvedClient.ToStringValue(),
                                            refundReason);
                                        if (!refundSuccess)
                                        {
                                            var adminUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                                            
                                            var lastCriticalLog = await _context.Logs
                                                .Include(l => l.LogType)
                                                .Where(l => l.RelatedEntityType == "SearchHire" && 
                                                           l.RelatedEntityId == dispute.SearchHire.Id &&
                                                           l.LogType != null &&
                                                           l.LogType.Name == "Critical" &&
                                                           l.Source != null &&
                                                           l.Source.Contains("ProcessMoneyDistributionAsync"))
                                                .OrderByDescending(l => l.CreatedAt)
                                                .FirstOrDefaultAsync();
                                            
                                            var errorDetails = lastCriticalLog != null 
                                                ? $"Last error from ProcessMoneyDistributionAsync: {lastCriticalLog.Message}. Details: {lastCriticalLog.Details}"
                                                : "No detailed error log found. Check ProcessMoneyDistributionAsync for missing config or Stripe errors.";
                                            
                                            await _loggingService.LogCriticalAsync(
                                                message: "CRITICAL: Failed to process client refund in dispute resolution",
                                                details: $"Failed to process money distribution for dispute {disputeId} (SearchHire {dispute.SearchHire.Id}) resolved in favor of client. " +
                                                        $"Status: {SearchHireStatus.DisputeResolvedClient.ToStringValue()}, Amount: {dispute.SearchHire.Amount}€, " +
                                                        $"ClientId: {dispute.SearchHire.ClientId}, ExpertId: {dispute.SearchHire.ExpertId}, " +
                                                        $"ResolutionComments: {request.ResolutionComments}. " +
                                                        $"{errorDetails}",
                                                userId: adminUserId,
                                                source: "DisputeController.ResolveDispute",
                                                relatedEntityType: "Dispute",
                                                relatedEntityId: disputeId,
                                                additionalData: new { 
                                                    DisputeId = disputeId,
                                                    SearchHireId = dispute.SearchHire.Id,
                                                    Status = SearchHireStatus.DisputeResolvedClient.ToStringValue(),
                                                    Amount = dispute.SearchHire.Amount,
                                                    ClientId = dispute.SearchHire.ClientId,
                                                    ExpertId = dispute.SearchHire.ExpertId,
                                                    ResolutionComments = request.ResolutionComments,
                                                    LastErrorLogId = lastCriticalLog?.Id
                                                }
                                            );
                                            
                                            var errorMessage = lastCriticalLog != null
                                                ? $"Failed to process client refund: {lastCriticalLog.Message}. Check logs for details (LogId: {lastCriticalLog.Id})"
                                                : $"Failed to process client refund. Possible causes: Missing money distribution config for status '{SearchHireStatus.DisputeResolvedClient.ToStringValue()}', Stripe payment intent not found, or insufficient balance. Check logs for details.";
                                            
                                            return StatusCode(500, new { 
                                                message = errorMessage,
                                                errorCode = "CLIENT_REFUND_FAILED",
                                                searchHireId = dispute.SearchHire.Id,
                                                status = SearchHireStatus.DisputeResolvedClient.ToStringValue(),
                                                amount = dispute.SearchHire.Amount,
                                                clientId = dispute.SearchHire.ClientId,
                                                logId = lastCriticalLog?.Id
                                            });
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        var adminUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL: Exception during client refund in dispute resolution",
                                            details: $"Exception occurred while processing money distribution for dispute {disputeId} (SearchHire {dispute.SearchHire.Id}) resolved in favor of client. " +
                                                    $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                                                    $"Stack Trace: {ex.StackTrace}. " +
                                                    $"Inner Exception: {ex.InnerException?.Message}. " +
                                                    $"Status: {SearchHireStatus.DisputeResolvedClient.ToStringValue()}, Amount: {dispute.SearchHire.Amount}€, " +
                                                    $"ClientId: {dispute.SearchHire.ClientId}.",
                                            userId: adminUserId,
                                            source: "DisputeController.ResolveDispute",
                                            relatedEntityType: "Dispute",
                                            relatedEntityId: disputeId,
                                            additionalData: new { 
                                                DisputeId = disputeId,
                                                SearchHireId = dispute.SearchHire.Id,
                                                ErrorType = ex.GetType().Name,
                                                ErrorMessage = ex.Message,
                                                StackTrace = ex.StackTrace,
                                                InnerException = ex.InnerException?.Message
                                            }
                                        );
                                        return StatusCode(500, new { 
                                            message = $"Failed to process client refund: {ex.Message}",
                                            errorCode = "CLIENT_REFUND_EXCEPTION",
                                            errorType = ex.GetType().Name,
                                            searchHireId = dispute.SearchHire.Id
                                        });
                                    }
                                    break;
                                }

                            case "pay_expert":
                                {
                                    try
                                    {
                                        var transferSuccess = await _refundService.ProcessMoneyDistributionAsync(
                                            dispute.SearchHire.Id,
                                            SearchHireStatus.DisputeResolvedExpert.ToStringValue(),
                                            "Dispute resolved in favor of expert");
                                        if (!transferSuccess)
                                        {
                                            var adminUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                                            
                                            var lastCriticalLog = await _context.Logs
                                                .Include(l => l.LogType)
                                                .Where(l => l.RelatedEntityType == "SearchHire" && 
                                                           l.RelatedEntityId == dispute.SearchHire.Id &&
                                                           l.LogType != null &&
                                                           l.LogType.Name == "Critical" &&
                                                           l.Source != null &&
                                                           l.Source.Contains("ProcessMoneyDistributionAsync"))
                                                .OrderByDescending(l => l.CreatedAt)
                                                .FirstOrDefaultAsync();
                                            
                                            var errorDetails = lastCriticalLog != null 
                                                ? $"Last error from ProcessMoneyDistributionAsync: {lastCriticalLog.Message}. Details: {lastCriticalLog.Details}"
                                                : "No detailed error log found. Check ProcessMoneyDistributionAsync for missing config or Stripe errors.";
                                            
                                            await _loggingService.LogCriticalAsync(
                                                message: "CRITICAL: Failed to process expert transfer in dispute resolution",
                                                details: $"Failed to process money distribution for dispute {disputeId} (SearchHire {dispute.SearchHire.Id}) resolved in favor of expert. " +
                                                        $"Status: {SearchHireStatus.DisputeResolvedExpert.ToStringValue()}, Amount: {dispute.SearchHire.Amount}€, " +
                                                        $"ClientId: {dispute.SearchHire.ClientId}, ExpertId: {dispute.SearchHire.ExpertId}, " +
                                                        $"ExpertStripeAccountId: {dispute.SearchHire.Expert?.ExpertProfile?.StripeAccountId ?? "NOT_SET"}, " +
                                                        $"ResolutionComments: {request.ResolutionComments}. " +
                                                        $"{errorDetails}",
                                                userId: adminUserId,
                                                source: "DisputeController.ResolveDispute",
                                                relatedEntityType: "Dispute",
                                                relatedEntityId: disputeId,
                                                additionalData: new { 
                                                    DisputeId = disputeId,
                                                    SearchHireId = dispute.SearchHire.Id,
                                                    Status = SearchHireStatus.DisputeResolvedExpert.ToStringValue(),
                                                    Amount = dispute.SearchHire.Amount,
                                                    ClientId = dispute.SearchHire.ClientId,
                                                    ExpertId = dispute.SearchHire.ExpertId,
                                                    ExpertStripeAccountId = dispute.SearchHire.Expert?.ExpertProfile?.StripeAccountId,
                                                    ResolutionComments = request.ResolutionComments,
                                                    LastErrorLogId = lastCriticalLog?.Id
                                                }
                                            );
                                            
                                            var errorMessage = lastCriticalLog != null
                                                ? $"Failed to process expert transfer: {lastCriticalLog.Message}. Check logs for details (LogId: {lastCriticalLog.Id})"
                                                : $"Failed to process expert transfer. Possible causes: Missing money distribution config for status '{SearchHireStatus.DisputeResolvedExpert.ToStringValue()}', Stripe account not configured, or insufficient balance. Check logs for details.";
                                            
                                            return StatusCode(500, new { 
                                                message = errorMessage,
                                                errorCode = "EXPERT_TRANSFER_FAILED",
                                                searchHireId = dispute.SearchHire.Id,
                                                status = SearchHireStatus.DisputeResolvedExpert.ToStringValue(),
                                                expertStripeAccountId = dispute.SearchHire.Expert?.ExpertProfile?.StripeAccountId ?? "NOT_SET",
                                                logId = lastCriticalLog?.Id
                                            });
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        var adminUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL: Exception during expert transfer in dispute resolution",
                                            details: $"Exception occurred while processing money distribution for dispute {disputeId} (SearchHire {dispute.SearchHire.Id}) resolved in favor of expert. " +
                                                    $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                                                    $"Stack Trace: {ex.StackTrace}. " +
                                                    $"Inner Exception: {ex.InnerException?.Message}. " +
                                                    $"Status: {SearchHireStatus.DisputeResolvedExpert.ToStringValue()}, Amount: {dispute.SearchHire.Amount}€, " +
                                                    $"ExpertStripeAccountId: {dispute.SearchHire.Expert?.ExpertProfile?.StripeAccountId ?? "NOT_SET"}.",
                                            userId: adminUserId,
                                            source: "DisputeController.ResolveDispute",
                                            relatedEntityType: "Dispute",
                                            relatedEntityId: disputeId,
                                            additionalData: new { 
                                                DisputeId = disputeId,
                                                SearchHireId = dispute.SearchHire.Id,
                                                ErrorType = ex.GetType().Name,
                                                ErrorMessage = ex.Message,
                                                StackTrace = ex.StackTrace,
                                                InnerException = ex.InnerException?.Message
                                            }
                                        );
                                        return StatusCode(500, new { 
                                            message = $"Failed to process expert transfer: {ex.Message}",
                                            errorCode = "EXPERT_TRANSFER_EXCEPTION",
                                            errorType = ex.GetType().Name,
                                            searchHireId = dispute.SearchHire.Id
                                        });
                                    }
                                    break;
                                }

                            default:
                                return BadRequest(new { message = "Invalid action. Valid actions: refund_client, pay_expert" });
                        }

                        if (request.Action.ToLower() == "refund_client")
                        {
                            await _loggingService.LogInfoAsync(
                                message: "Disputa resuelta a tu favor",
                                details: $"La disputa del servicio #{dispute.SearchHire.Id} se resolvió a tu favor. Se procesará tu reembolso de {dispute.SearchHire.Amount:F2}€.",
                                userId: dispute.SearchHire.ClientId,
                                source: "DisputeController.ResolveDispute",
                                relatedEntityType: "Dispute",
                                relatedEntityId: dispute.Id,
                                notifyUser: true
                            );

                            if (dispute.SearchHire.ExpertId.HasValue)
                            {
                                await _loggingService.LogWarningAsync(
                                    message: "Disputa resuelta a favor del cliente",
                                    details: $"La disputa del servicio #{dispute.SearchHire.Id} se resolvió a favor del cliente. El reembolso se procesará.",
                                    userId: dispute.SearchHire.ExpertId.Value,
                                    source: "DisputeController.ResolveDispute",
                                    relatedEntityType: "Dispute",
                                    relatedEntityId: dispute.Id,
                                    notifyUser: true
                                );
                            }
                        }
                        else if (request.Action.ToLower() == "pay_expert")
                        {
                            // Disputa resuelta a favor del experto
                            if (dispute.SearchHire.ExpertId.HasValue)
                            {
                                await _loggingService.LogInfoAsync(
                                    message: "Disputa resuelta a tu favor",
                                    details: $"La disputa del servicio #{dispute.SearchHire.Id} se resolvió a tu favor. Has recibido {dispute.SearchHire.Amount:F2}€.",
                                    userId: dispute.SearchHire.ExpertId.Value,
                                    source: "DisputeController.ResolveDispute",
                                    relatedEntityType: "Dispute",
                                    relatedEntityId: dispute.Id,
                                    notifyUser: true
                                );
                            }

                            await _loggingService.LogWarningAsync(
                                message: "Disputa resuelta a favor del experto",
                                details: $"La disputa del servicio #{dispute.SearchHire.Id} se resolvió a favor del experto.",
                                userId: dispute.SearchHire.ClientId,
                                source: "DisputeController.ResolveDispute",
                                relatedEntityType: "Dispute",
                                relatedEntityId: dispute.Id,
                                notifyUser: true
                            );
                        }

                        return Ok(new { message = "Dispute resolved successfully" });
                    }
                    catch (Exception ex)
                    {
                        var adminUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Exception during dispute resolution",
                            details: $"Exception occurred while resolving dispute {disputeId}. " +
                                    $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                                    $"Stack Trace: {ex.StackTrace}. " +
                                    $"Inner Exception: {ex.InnerException?.Message}. " +
                                    $"ACTION REQUIRED: Review exception details and fix the underlying issue.",
                            userId: adminUserId,
                            source: "DisputeController.ResolveDispute",
                            relatedEntityType: "Dispute",
                            relatedEntityId: disputeId,
                            additionalData: new { 
                                DisputeId = disputeId,
                                ErrorType = ex.GetType().Name,
                                ErrorMessage = ex.Message,
                                StackTrace = ex.StackTrace,
                                InnerException = ex.InnerException?.Message
                            }
                        );
                        return StatusCode(500, new { message = "Error resolving dispute" });
                    }
                });
            }
            catch (Exception ex)
            {
                // 🚨 LOG CRÍTICO: Excepción externa durante resolución de disputa
                var adminUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: External exception during dispute resolution",
                    details: $"External exception occurred while resolving dispute. " +
                            $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                            $"Stack Trace: {ex.StackTrace}. " +
                            $"Inner Exception: {ex.InnerException?.Message}. " +
                            $"ACTION REQUIRED: Review exception details and fix the underlying issue.",
                    userId: adminUserId,
                    source: "DisputeController.ResolveDispute",
                    relatedEntityType: "Dispute",
                    relatedEntityId: null,
                    additionalData: new { 
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );
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
                    StatusTranslated = DisputeStatusExtensions.ToSpanishTranslation(dispute.Status),
                    ResolutionComments = dispute.ResolutionComments,
                    CreatedAt = dispute.CreatedAt,
                    SearchHire = new SearchHireInfoDto
                    {
                        Id = dispute.SearchHire.Id,
                        Status = dispute.SearchHire.Status.StatusValue,
                        StatusTranslated = SearchHireStatusExtensions.ToSpanishTranslation(dispute.SearchHire.Status.StatusValue),
                        Amount = dispute.SearchHire.Amount,
                        CreatedAt = dispute.SearchHire.CreatedAt
                    },
                    Reporter = new UserDto
                    {
                        Id = dispute.Reporter.Id,
                        Name = dispute.Reporter.Name,
                        Email = dispute.Reporter.Email
                    },
                    Client = dispute.SearchHire.ClientId.HasValue && dispute.SearchHire.Client != null ? new UserDto
                    {
                        Id = dispute.SearchHire.Client.Id,
                        Name = dispute.SearchHire.Client.Name,
                        Email = dispute.SearchHire.Client.Email
                    } : null, // ✅ Manejar caso donde ClientId es null (usuario eliminado)
                    Expert = dispute.SearchHire.ExpertId.HasValue && dispute.SearchHire.Expert != null ? new UserDto
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
                        StatusTranslated = SearchHireStatusExtensions.ToSpanishTranslation(dispute.SearchHire.Status.StatusValue),
                        CreatedAt = dispute.SearchHire.CreatedAt,
                        ExpertTimezone = dispute.SearchHire.ExpertTimezone, // ✅ INTERNACIONALIZACIÓN
                        ExpertCountry = dispute.SearchHire.ExpertCountry, // ✅ INTERNACIONALIZACIÓN
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
                    .Include(sh => sh.Status)
                    .FirstOrDefaultAsync(sh => sh.Id == request.SearchHireId);

                if (searchHire == null)
                {
                    return NotFound(new { message = "Service not found" });
                }
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
                        _context.Disputes.Add(dispute);
                        await _context.SaveChangesAsync();
                        // Actualizar el estado del SearchHire a Disputed
                        searchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Disputed.ToStringValue());
                        await _context.SaveChangesAsync();
                        
                        await transaction.CommitAsync();
                        // Guardar el ID para uso posterior
                        disputeId = dispute.Id;
                    }
                    catch
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                });

                // ✅ Notificar a la otra parte sobre la disputa creada
                if (searchHire.ClientId == userId)
                {
                    // Cliente creó la disputa - notificar al experto
                    if (searchHire.ExpertId.HasValue)
                    {
                        await _loggingService.LogWarningAsync(
                            message: "Disputa abierta por el cliente",
                            details: $"El cliente ha abierto una disputa sobre el servicio #{searchHire.Id}. Tienes 48 horas para responder. Razón: {request.Reason}",
                            userId: searchHire.ExpertId.Value,
                            source: "DisputeController.CreateDisputeWithFiles",
                            relatedEntityType: "Dispute",
                            relatedEntityId: disputeId,
                            notifyUser: true
                        );
                    }
                }
                else if (searchHire.ExpertId == userId)
                {
                    // Experto creó la disputa - notificar al cliente
                    await _loggingService.LogWarningAsync(
                        message: "Disputa abierta por el experto",
                        details: $"El experto ha abierto una disputa sobre el servicio #{searchHire.Id}. Un administrador la revisará. Razón: {request.Reason}",
                        userId: searchHire.ClientId,
                        source: "DisputeController.CreateDisputeWithFiles",
                        relatedEntityType: "Dispute",
                        relatedEntityId: disputeId,
                        notifyUser: true
                    );
                }

                // Handle file uploads if any (outside transaction for better performance)
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
                                        // ✅ FIX: Quitar PredefinedAcl cuando el bucket tiene uniform bucket-level access habilitado
                                        // El acceso se controla mediante IAM policies del bucket, no ACLs por objeto
                                        // options: new UploadObjectOptions { PredefinedAcl = PredefinedObjectAcl.Private }
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
                return Ok(new { message = "Dispute opened successfully", disputeId = disputeId });
            }
            catch (Exception ex)
            {
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
                    StatusTranslated = DisputeStatusExtensions.ToSpanishTranslation(dispute.Status),
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
                        StatusTranslated = SearchHireStatusExtensions.ToSpanishTranslation(dispute.SearchHire.Status.StatusValue),
                        Amount = dispute.SearchHire.Amount,
                        CreatedAt = dispute.SearchHire.CreatedAt
                    },
                    Reporter = new UserDto
                    {
                        Id = dispute.Reporter.Id,
                        Name = dispute.Reporter.Name,
                        Email = dispute.Reporter.Email
                    },
                    Client = dispute.SearchHire.ClientId.HasValue && dispute.SearchHire.Client != null ? new UserDto
                    {
                        Id = dispute.SearchHire.Client.Id,
                        Name = dispute.SearchHire.Client.Name,
                        Email = dispute.SearchHire.Client.Email
                    } : null, // ✅ Manejar caso donde ClientId es null (usuario eliminado)
                    Expert = dispute.SearchHire.ExpertId.HasValue && dispute.SearchHire.Expert != null ? new UserDto
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
                    Files = dispute.Files.Select(f =>
                    {
                        var signedFileUrl = ResolveDisputeFileUrl(f);
                        return new DisputeFileDto
                        {
                            Id = f.Id,
                            FileName = f.FileName,
                            FileType = f.FileType,
                            FileSize = f.FileSize,
                            CreatedAt = f.CreatedAt,
                            FilePath = signedFileUrl,
                            FileUrl = signedFileUrl,
                            UploadedByUserId = f.UploadedByUserId,
                            UploadedByUserName = f.UploadedByUser?.Name ?? "Usuario desconocido",
                            UploadedByUserEmail = f.UploadedByUser?.Email ?? "",
                            FileCategory = f.FileCategory,
                            FileCategoryLabel = f.FileCategory == "client" ? "Archivo del Cliente" : "Archivo del Experto"
                        };
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

                            await _storageClient.UploadObjectAsync(
                                bucketName,
                                fileName,
                                file.ContentType,
                                memoryStream
                                // ✅ FIX: Quitar PredefinedAcl cuando el bucket tiene uniform bucket-level access habilitado
                                // El acceso se controla mediante IAM policies del bucket, no ACLs por objeto
                                // options: new UploadObjectOptions { PredefinedAcl = PredefinedObjectAcl.Private }
                                );

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
                            return StatusCode(500, new { message = "Failed to upload file" });
                        }
                    }

                    if (disputeFiles.Any())
                    {
                        _context.DisputeFiles.AddRange(disputeFiles);
                        await _context.SaveChangesAsync();
                    }
                }
                return Ok(new { message = "Expert response submitted successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        private string ResolveDisputeFileUrl(DisputeFile? file)
        {
            if (file == null)
            {
                return string.Empty;
            }

            var fallback = string.IsNullOrWhiteSpace(file.FilePath) ? string.Empty : file.FilePath;
            var objectName = ExtractObjectNameFromUrl(file.FilePath);
            if (string.IsNullOrEmpty(objectName))
            {
                return fallback;
            }

            return _signedUrlService.GetSignedUrl(objectName) ?? fallback;
        }

        private string? ExtractObjectNameFromUrl(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            try
            {
                var bucketName = _configuration["GoogleCloud:BucketName"];
                if (!string.IsNullOrWhiteSpace(bucketName))
                {
                    var prefix = $"https://storage.googleapis.com/{bucketName}/";
                    if (filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return filePath[prefix.Length..];
                    }
                }

                var uri = new Uri(filePath);
                return uri.AbsolutePath.TrimStart('/');
            }
            catch
            {
                return null;
            }
        }

    }
}