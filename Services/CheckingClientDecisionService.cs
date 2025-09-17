using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using newApi.DataLayer;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Common;
using Stripe;

namespace newApi.Services
{
    public interface ICheckingClientDecisionService
    {
        Task ProcessTransferToExpert(int searchHireId);
        Task<bool> ValidateStateTransition(string currentStatus, string newStatus);
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
            Transfer? transfer = null;
            
            // Check if we're already in a transaction
            var existingTransaction = _context.Database.CurrentTransaction;
            IDbContextTransaction? transaction = null;
            
            if (existingTransaction == null)
            {
                transaction = await _context.Database.BeginTransactionAsync();
            }
            
            try
            {
                // 1. Validar que el SearchHire existe
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Expert)
                    .ThenInclude(e => e.ExpertProfile)
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId);

                if (searchHire == null)
                {
                    _logger.LogError("SearchHire not found for searchHireId={SearchHireId}", searchHireId);
                    throw new InvalidOperationException("SearchHire not found");
                }

                // 2. Validar estado del SearchHire
                if (searchHire.Status != "awaiting_client_decision")
                {
                    _logger.LogError("Invalid status for transfer: searchHireId={SearchHireId}, status={Status}", searchHireId, searchHire.Status);
                    throw new InvalidOperationException($"Invalid status for transfer: {searchHire.Status}");
                }

                // 3. Validar que el cliente aprobó
                if (searchHire.ClientApproved != true)
                {
                    _logger.LogError("Client has not approved: searchHireId={SearchHireId}, clientApproved={ClientApproved}", searchHireId, searchHire.ClientApproved);
                    throw new InvalidOperationException("Client must approve before transfer");
                }

                // 4. Validar que no se haya transferido ya
                if (!string.IsNullOrEmpty(searchHire.ExpertTransferId))
                {
                    _logger.LogError("Transfer already exists: searchHireId={SearchHireId}, transferId={TransferId}", searchHireId, searchHire.ExpertTransferId);
                    throw new InvalidOperationException("Transfer already processed");
                }

                // 5. Validar cuenta Stripe del experto
                var expertStripeAccountId = searchHire.Expert?.ExpertProfile?.StripeAccountId;
                if (string.IsNullOrEmpty(expertStripeAccountId))
                {
                    _logger.LogError("Expert has no Stripe account for searchHireId={SearchHireId}, expertId={ExpertId}", searchHireId, searchHire.ExpertId);
                    throw new InvalidOperationException("Expert has no Stripe account configured");
                }

                // 6. Calcular montos
                var commissionRate = 0.1m;
                var amountToExpert = searchHire.Amount * (1 - commissionRate);
                var amountInCents = (long)(amountToExpert * 100);

                if (amountInCents <= 0)
                {
                    _logger.LogError("Invalid amount for transfer: searchHireId={SearchHireId}, amount={Amount}", searchHireId, amountToExpert);
                    throw new InvalidOperationException("Invalid transfer amount");
                }

                // 7. Crear transferencia en Stripe
                var transferOptions = new TransferCreateOptions
                {
                    Amount = amountInCents,
                    Currency = "eur",
                    Destination = expertStripeAccountId,
                    Metadata = new Dictionary<string, string>
                    {
                        { "searchHireId", searchHireId.ToString() },
                        { "expertId", searchHire.ExpertId?.ToString() ?? "unknown" }
                    }
                };

                var transferService = new TransferService();
                transfer = await transferService.CreateAsync(transferOptions);
                
                _logger.LogInformation("Transfer created in Stripe: searchHireId={SearchHireId}, transferId={TransferId}, amount={Amount}", 
                    searchHireId, transfer.Id, amountToExpert);

                // 8. Actualizar SearchHire con el ID de transferencia
                searchHire.ExpertTransferId = transfer.Id;
                searchHire.UpdatedAt = DateTime.UtcNow;

                // 9. Crear registro financiero
                var financialTransaction = new FinancialTransaction
                {
                    UserId = searchHire.ExpertId ?? 0,
                    Amount = amountToExpert,
                    TransactionType = "Payout",
                    RelatedEntityType = "SearchHire",
                    RelatedEntityId = searchHireId,
                    CreatedAt = DateTime.UtcNow,
                    StripeTransferId = transfer.Id
                };
                _context.FinancialTransactions.Add(financialTransaction);

                // 10. Guardar todos los cambios en la base de datos
                await _context.SaveChangesAsync();

                // 11. Confirmar transacción (solo si creamos una nueva)
                if (transaction != null)
                {
                    await transaction.CommitAsync();
                }
                
                _logger.LogInformation("Transfer processed successfully: searchHireId={SearchHireId}, transferId={TransferId}", 
                    searchHireId, transfer.Id);
            }
            catch (Exception ex)
            {
                // 12. Rollback de la transacción de base de datos (solo si creamos una nueva)
                if (transaction != null)
                {
                    await transaction.RollbackAsync();
                }
                
                // 13. Manejar transferencias fallidas
                if (transfer != null)
                {
                    try
                    {
                        // Nota: Stripe no permite cancelar transferencias una vez creadas
                        // Marcar como TransferFailed para revisión manual
                        var searchHire = await _context.SearchHires.FindAsync(searchHireId);
                        if (searchHire != null)
                        {
                            searchHire.Status = SearchHireStatus.TransferFailed.ToStringValue();
                            searchHire.UpdatedAt = DateTime.UtcNow;
                            await _context.SaveChangesAsync();
                            
                            _logger.LogError("Transfer created but database operation failed: transferId={TransferId}, searchHireId={SearchHireId}. Marked as TransferFailed for manual review.", 
                                transfer.Id, searchHireId);
                        }
                    }
                    catch (Exception updateEx)
                    {
                        _logger.LogError(updateEx, "Error updating SearchHire status to TransferFailed: searchHireId={SearchHireId}", searchHireId);
                    }
                }
                else
                {
                    // Si no se creó transferencia, marcar como TransferFailed también
                    try
                    {
                        var searchHire = await _context.SearchHires.FindAsync(searchHireId);
                        if (searchHire != null)
                        {
                            searchHire.Status = SearchHireStatus.TransferFailed.ToStringValue();
                            searchHire.UpdatedAt = DateTime.UtcNow;
                            await _context.SaveChangesAsync();
                            
                            _logger.LogError("Transfer failed before creation: searchHireId={SearchHireId}. Marked as TransferFailed.", searchHireId);
                        }
                    }
                    catch (Exception updateEx)
                    {
                        _logger.LogError(updateEx, "Error updating SearchHire status to TransferFailed: searchHireId={SearchHireId}", searchHireId);
                    }
                }
                
                _logger.LogError(ex, "Failed to process transfer: searchHireId={SearchHireId}", searchHireId);
                throw;
            }
            finally
            {
                // Dispose transaction if we created one
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }
            }
        }

        public async Task<bool> ValidateStateTransition(string currentStatus, string newStatus)
        {
            // Definir transiciones válidas
            var validTransitions = new Dictionary<string, List<string>>
            {
                { "pending", new List<string> { "awaiting_client_decision", "cancelled", "transfer_failed" } },
                { "awaiting_client_decision", new List<string> { "completed", "disputed", "transfer_failed" } },
                { "disputed", new List<string> { "completed", "dispute-resolved", "transfer_failed" } },
                { "completed", new List<string> { } }, // Estado final
                { "cancelled", new List<string> { } }, // Estado final
                { "transfer_failed", new List<string> { "completed", "cancelled" } }, // Puede ser resuelto manualmente
                { "dispute-resolved", new List<string> { } } // Estado final
            };

            if (!validTransitions.ContainsKey(currentStatus))
            {
                _logger.LogError("Invalid current status: {CurrentStatus}", currentStatus);
                return false;
            }

            var allowedTransitions = validTransitions[currentStatus];
            if (!allowedTransitions.Contains(newStatus))
            {
                _logger.LogError("Invalid transition from {CurrentStatus} to {NewStatus}", currentStatus, newStatus);
                return false;
            }

            return true;
        }
    }
}