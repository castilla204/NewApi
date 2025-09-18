using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using newApi.DataLayer;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
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


                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId);

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", searchHireId);
                    throw new Exception("SearchHire not found");
                }

                var commissionRate = 0.1m;
                var amountToExpert = searchHire.Amount * (1 - commissionRate);
                var amountInCents = (long)(amountToExpert * 100);

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



                var financialTransaction = new FinancialTransaction
                {
                    UserId = searchHire.ExpertId ?? 0,
                    Amount = amountToExpert,
                    TransactionType = "Payout",
                    RelatedEntityType = "SearchHire",
                    RelatedEntityId = searchHireId,
                    CreatedAt = DateTime.UtcNow
                };
                _context.FinancialTransactions.Add(financialTransaction);


                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                throw ex;
            }

        }
    }
}
