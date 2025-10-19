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

        public CheckingClientDecisionService(AppDbContext context, ILogger<CheckingClientDecisionService> logger, SystemStatusService systemStatusService)
        {
            _context = context;
            _logger = logger;
            _systemStatusService = systemStatusService;
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

                // 🎯 USAR SISTEMA DE CONFIGURACIONES EN LUGAR DE COMISIÓN FIJA
                var config = await GetMoneyDistributionConfigAsync("appointment_awaiting_report", 
                    searchHire.SearchService?.CategoryId, 
                    searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);
                
                if (config == null)
                {
                    _logger.LogError("No money distribution configuration found for searchHireId={SearchHireId}", searchHireId);
                    throw new Exception("No money distribution configuration found");
                }
                
                var amountToExpert = searchHire.Amount * (config.ExpertPercentage / 100);
                var amountInCents = (long)(amountToExpert * 100);
                
                _logger.LogInformation("Using money distribution config: Expert={ExpertPercentage}%, Platform={PlatformPercentage}%, Source={Source} for searchHireId={SearchHireId}", 
                    config.ExpertPercentage, config.PlatformPercentage, config.Source, searchHireId);

                var expertStripeAccountId = searchHire.Expert?.ExpertProfile?.StripeAccountId;
                if (string.IsNullOrEmpty(expertStripeAccountId))
                {
                    _logger.LogError("Expert has no Stripe account for searchHireId={SearchHireId}, expertId={ExpertId}", searchHireId, searchHire.ExpertId);
                    throw new Exception("Expert has no Stripe account configured");
                }

                var transferOptions = new TransferCreateOptions
                {
                    Amount = amountInCents,
                    Currency = "eur",
                    Destination = expertStripeAccountId,
                    Metadata = new Dictionary<string, string>
                {
                    { "searchHireId", searchHireId.ToString() }
                }
                };

                var transferService = new TransferService();
                var transfer = await transferService.CreateAsync(transferOptions);
                searchHire.ExpertTransferId = transfer.Id;
                _logger.LogInformation("Transfer created for searchHireId={SearchHireId}, transferId={TransferId}, amount={Amount}", searchHireId, transfer.Id, amountToExpert);



                var expertTransaction = new FinancialTransaction
                {
                    UserId = searchHire.ExpertId ?? 0,
                    Amount = amountToExpert,
                    TransactionType = "Payout",
                    RelatedEntityType = "SearchHire",
                    RelatedEntityId = searchHireId,
                    CreatedAt = DateTime.UtcNow
                };
                _context.FinancialTransactions.Add(expertTransaction);

                // 🚨 ACTUALIZAR BALANCE DEL CLIENTE (dinero ya se retiró al contratar)
                // El balance del cliente ya se redujo cuando contrató el servicio
                // Aquí solo registramos la transacción de pago al experto

                // NO hacer SaveChanges aquí - se hace en el código que llama
                // await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                throw ex;
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
