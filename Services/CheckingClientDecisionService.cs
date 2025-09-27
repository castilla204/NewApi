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

        public CheckingClientDecisionService(AppDbContext context, ILogger<CheckingClientDecisionService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task ProcessTransferToExpert(int searchHireId)
        {
            try
            {
                // 🔒 ROW-LEVEL LOCKING para prevenir race conditions
                var searchHire = await _context.SearchHires
                    .FromSqlRaw("SELECT * FROM \"SearchHires\" WHERE \"Id\" = {0} FOR UPDATE", searchHireId)
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
            if (searchHire.Status != SearchHireStatus.Pending.ToStringValue() && 
                searchHire.Status != SearchHireStatus.AwaitingClientDecision.ToStringValue())
            {
                _logger.LogWarning("SearchHire is not in valid status for transfer for searchHireId={SearchHireId}, current status={Status}", 
                    searchHireId, searchHire.Status);
                throw new Exception($"SearchHire is not in valid status for transfer: {searchHire.Status}");
            }

            // 🚨 PROTECCIÓN CONTRA TRANSFERENCIAS DUPLICADAS
            if (!string.IsNullOrEmpty(searchHire.ExpertTransferId))
            {
                _logger.LogWarning("Transfer already exists for searchHireId={SearchHireId}, transferId={TransferId}", 
                    searchHireId, searchHire.ExpertTransferId);
                throw new Exception($"Transfer already exists for this SearchHire: {searchHire.ExpertTransferId}");
            }

                // 🎯 USAR SISTEMA DE CONFIGURACIONES EN LUGAR DE COMISIÓN FIJA
                var config = await GetMoneyDistributionConfigAsync("appointment_completed", 
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
            // 1. Buscar configuración específica por Category + ServiceTypeCategory
            if (categoryId.HasValue && serviceTypeCategoryId.HasValue)
            {
                var specificConfig = await _context.CategoryServiceTypeConfigs
                    .Include(cst => cst.Category)
                    .Include(cst => cst.ServiceTypeCategory)
                    .FirstOrDefaultAsync(cst => cst.CategoryId == categoryId.Value 
                                             && cst.ServiceTypeCategoryId == serviceTypeCategoryId.Value 
                                             && cst.Status == status 
                                             && cst.IsActive);

                if (specificConfig != null)
                {
                    return new MoneyDistributionConfigDto
                    {
                        ClientPercentage = specificConfig.ClientPercentage,
                        ExpertPercentage = specificConfig.ExpertPercentage,
                        PlatformPercentage = specificConfig.PlatformPercentage,
                        Source = "category_service_type",
                        CategoryName = specificConfig.Category?.Name,
                        ServiceTypeCategoryName = specificConfig.ServiceTypeCategory?.Name,
                        Status = status
                    };
                }
            }

            // 2. Buscar configuración específica por ServiceTypeCategory
            if (serviceTypeCategoryId.HasValue)
            {
                var categoryConfig = await _context.ServiceTypeCategoryConfigs
                    .Include(sc => sc.ServiceTypeCategory)
                    .FirstOrDefaultAsync(sc => sc.ServiceTypeCategoryId == serviceTypeCategoryId.Value 
                                             && sc.Status == status 
                                             && sc.IsActive);

                if (categoryConfig != null)
                {
                    return new MoneyDistributionConfigDto
                    {
                        ClientPercentage = categoryConfig.ClientPercentage,
                        ExpertPercentage = categoryConfig.ExpertPercentage,
                        PlatformPercentage = categoryConfig.PlatformPercentage,
                        Source = "service_type_category",
                        ServiceTypeCategoryName = categoryConfig.ServiceTypeCategory?.Name,
                        Status = status
                    };
                }
            }

            // 3. Buscar configuración por defecto por estado
            var defaultConfig = await _context.AppointmentStatusConfigs
                .FirstOrDefaultAsync(ac => ac.Status == status && ac.IsActive);

            if (defaultConfig != null)
            {
                return new MoneyDistributionConfigDto
                {
                    ClientPercentage = defaultConfig.ClientPercentage,
                    ExpertPercentage = defaultConfig.ExpertPercentage,
                    PlatformPercentage = defaultConfig.PlatformPercentage,
                    Source = "appointment_status",
                    Status = status
                };
            }

            // 4. NO HAY CONFIGURACIÓN - FALLAR EN LUGAR DE INVENTAR VALORES
            _logger.LogError("No money distribution configuration found for status: {Status}, categoryId: {CategoryId}, serviceTypeCategoryId: {ServiceTypeCategoryId}. Configuration must be created by admin.", 
                status, categoryId, serviceTypeCategoryId);
            return null;
        }
    }
}
