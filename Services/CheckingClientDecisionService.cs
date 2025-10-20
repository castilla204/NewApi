using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using newApi.DataLayer;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;
using newApi.Common;
using Stripe;

namespace newApi.Services
{
    public interface ICheckingClientDecisionService
    {
        Task ProcessTransferToExpert(int searchHireId);
    }

    public class CheckingClientDecisionService : ICheckingClientDecisionService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<CheckingClientDecisionService> _logger;
        private readonly SystemStatusService _systemStatusService;
        private readonly StripeRefundService _refundService;
        private readonly ILoggingService _loggingService;

        public CheckingClientDecisionService(AppDbContext context, ILogger<CheckingClientDecisionService> logger, SystemStatusService systemStatusService, ILoggingService loggingService, StripeRefundService refundService)
        {
            _context = context;
            _logger = logger;
            _systemStatusService = systemStatusService;
            _loggingService = loggingService;
            _refundService = refundService;
        }

        public async Task ProcessTransferToExpert(int searchHireId)
        {
            try
            {
                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var searchHire = await _context.SearchHires
                    .FromSqlRaw("SELECT * FROM \"SearchHires\" WHERE \"Id\" = {0} FOR UPDATE", searchHireId)
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .Include(sh => sh.SearchService)
                    .ThenInclude(ss => ss.ServiceType)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", searchHireId);
                    throw new Exception("SearchHire not found");
                }

            // Verificar que el servicio esté en estado válido para transferencia
            if (searchHire.Status.StatusValue != SearchHireStatus.Pending.ToStringValue() && 
                searchHire.Status.StatusValue != SearchHireStatus.AwaitingClientDecision.ToStringValue())
            {
                _logger.LogWarning("SearchHire is not in valid status for transfer for searchHireId={SearchHireId}, current status={Status}", 
                    searchHireId, searchHire.Status.StatusValue);
                throw new Exception($"SearchHire is not in valid status for transfer: {searchHire.Status.StatusValue}");
            }

            // 🚨 PROTECCIÓN CONTRA TRANSFERENCIAS DUPLICADAS
            if (!string.IsNullOrEmpty(searchHire.ExpertTransferId))
            {
                _logger.LogWarning("Transfer already exists for searchHireId={SearchHireId}, transferId={TransferId}", 
                    searchHireId, searchHire.ExpertTransferId);
                throw new Exception($"Transfer already exists for this SearchHire: {searchHire.ExpertTransferId}");
            }

                // Orquestar distribución según configuración: usar estado final 'completed'
                var ok = await _refundService.ProcessMoneyDistributionAsync(
                    searchHireId,
                    "completed",
                    "Auto transfer after client decision",
                    null);

                if (!ok)
                {
                    throw new Exception("Money distribution orchestration failed");
                }
            }
            catch (Exception ex)
            {
                // Log critical error for money transaction failure
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Error processing transfer to expert",
                    details: ex.ToString(),
                    source: "CheckingClientDecisionService.ProcessTransferToExpert",
                    relatedEntityType: "Transfer",
                    relatedEntityId: searchHireId,
                    additionalData: new { 
                        SearchHireId = searchHireId,
                        ErrorMessage = ex.Message
                    }
                );
                
                throw;
            }

        }

        private async Task<MoneyDistributionConfigDto?> GetMoneyDistributionConfigAsync(string status, int? categoryId, int? serviceTypeCategoryId)
        {
            // Usar el nuevo sistema centralizado de estados
            var config = await _systemStatusService.GetMoneyDistributionAsync(status, categoryId, serviceTypeCategoryId);
            
            if (config != null)
            {
                return new MoneyDistributionConfigDto
                {
                    ClientPercentage = config.ClientPercentage,
                    ExpertPercentage = config.ExpertPercentage,
                    PlatformPercentage = config.PlatformPercentage,
                    Source = "centralized_status_system",
                    Status = status
                };
            }

            // NO HAY CONFIGURACIÓN - FALLAR EN LUGAR DE INVENTAR VALORES
            _logger.LogError("No money distribution configuration found for status: {Status}, categoryId: {CategoryId}, serviceTypeCategoryId: {ServiceTypeCategoryId}. Configuration must be created by admin.", 
                status, categoryId, serviceTypeCategoryId);
            return null;
        }
    }
}
