using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace NewApi.Tests.Builders;

/// <summary>
/// Crea un SearchHire mínimo. StatusId se resuelve por StatusValue contra el seed,
/// así no hay que hardcodear IDs (que cambian si se re-aplica el seed).
/// </summary>
public class SearchHireBuilder
{
    private int? _clientId;
    private int? _expertId;
    private int? _serviceId;
    private string _statusValue = "pending";
    private decimal _amount = 100m;
    private string _currency = "EUR";
    private decimal? _baseAmount;
    private decimal? _taxAmount;
    private string? _expertTransferId;
    private bool? _clientApproved;

    public SearchHireBuilder ForClient(int userId) { _clientId = userId; return this; }
    public SearchHireBuilder ForExpert(int userId) { _expertId = userId; return this; }
    public SearchHireBuilder ForService(int serviceId) { _serviceId = serviceId; return this; }
    public SearchHireBuilder WithStatusValue(string statusValue) { _statusValue = statusValue; return this; }
    public SearchHireBuilder WithAmount(decimal amount, string currency = "EUR")
    {
        _amount = amount;
        _currency = currency;
        return this;
    }
    public SearchHireBuilder WithTax(decimal baseAmount, decimal taxAmount)
    {
        _baseAmount = baseAmount;
        _taxAmount = taxAmount;
        return this;
    }
    /// <summary>Stripe payment intent vive en FinancialTransaction, no en SearchHire directamente.</summary>
    public SearchHireBuilder WithExpertTransferId(string transferId) { _expertTransferId = transferId; return this; }
    public SearchHireBuilder ClientApproved(bool approved = true) { _clientApproved = approved; return this; }

    public async Task<SearchHire> PersistAsync(AppDbContext db)
    {
        if (_serviceId is null)
            throw new InvalidOperationException("SearchHireBuilder.ForService(serviceId) requerido.");

        var status = await db.SystemStatuses
            .FirstOrDefaultAsync(s => s.StatusType == "SearchHireStatus" && s.StatusValue == _statusValue);

        if (status is null)
            throw new InvalidOperationException(
                $"Estado '{_statusValue}' no encontrado en SystemStatuses. ¿Seed cargado?");

        var hire = new SearchHire
        {
            ClientId = _clientId,
            ExpertId = _expertId,
            SearchServiceId = _serviceId.Value,
            StatusId = status.Id,
            Amount = _amount,
            Currency = _currency,
            BaseAmount = _baseAmount,
            TaxAmount = _taxAmount,
            ExpertTransferId = _expertTransferId,
            ClientApproved = _clientApproved,
            CreatedAt = DateTime.UtcNow,
        };

        db.SearchHires.Add(hire);
        await db.SaveChangesAsync();
        return hire;
    }
}
