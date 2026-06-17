using Microsoft.EntityFrameworkCore;
using Moq;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;

namespace NewApi.Tests.Integration;

/// <summary>
/// DAC7 · agregador anual (Dac7AnnualReportService.GenerateForYearAsync).
///
/// Valida el fix recién aplicado:
///   (1) los reembolsos/chargebacks se RESTAN del bruto del hire (consideration
///       paid OR credited → un hire reembolsado no es consideración percibida).
///   (2) los hires en moneda extranjera se CONVIERTEN a EUR vía IExchangeRateService
///       a la tasa de la fecha del hire (CreatedAt), igual que las platform fees
///       de ese hire (que están en la misma divisa que el hire).
///
/// Se siembra el chain real (User+ExpertProfile+SearchService+SearchHire) y se
/// persisten FinancialTransactions ligadas por (RelatedEntityType="SearchHire",
/// RelatedEntityId=hire.Id). El servicio se ejecuta contra el Postgres del
/// testcontainer; los asserts releen los snapshots desde un DbContext NUEVO.
///
/// IExchangeRateService e ILoggingService se mockean con Moq.
/// </summary>
public class Dac7AnnualReportTests : IntegrationTestBase
{
    public Dac7AnnualReportTests(PostgresContainerFixture fixture) : base(fixture) { }

    // Estado de finalización (IsFinalizationStatus=true en el seed). "completed" es
    // un estado de finalización canónico → el agregador incluye el hire.
    private const string FinalizationStatusValue = "completed";

    /// <summary>
    /// Mock de IExchangeRateService que replica el contrato del servicio real:
    /// GBP→EUR = 2.0, y cualquier from==to (incluido EUR→EUR) = 1.0. El 3er
    /// argumento (asOf) se ignora con It.IsAny&lt;DateTime?&gt;().
    /// </summary>
    private static Mock<IExchangeRateService> BuildExchangeRateMock()
    {
        var mock = new Mock<IExchangeRateService>();

        // Genérico primero: cualquier par devuelve 1.0 (replica from==to del real).
        mock.Setup(s => s.GetRateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(1.0m);

        // Específico GBP→EUR=2.0 DESPUÉS del genérico para que Moq lo priorice
        // (last-match-wins en setups solapados con It.IsAny).
        mock.Setup(s => s.GetRateAsync("GBP", "EUR", It.IsAny<DateTime?>()))
            .ReturnsAsync(2.0m);

        return mock;
    }

    private static Mock<ILoggingService> BuildLoggingMock()
    {
        var mock = new Mock<ILoggingService>();
        mock.Setup(l => l.LogInfoAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<object?>(), It.IsAny<bool>(),
                It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    /// <summary>
    /// Inserta una FinancialTransaction ligada a un hire (RelatedEntityType="SearchHire").
    /// El builder no cubre FinancialTransactions, así que se construye la entidad directamente.
    /// </summary>
    private static async Task AddFinancialTransactionAsync(
        AppDbContext db, int hireId, int? userId, string transactionType,
        decimal amount, string currency, DateTime createdAt)
    {
        db.FinancialTransactions.Add(new FinancialTransaction
        {
            UserId = userId,
            TransactionType = transactionType,
            RelatedEntityType = "SearchHire",
            RelatedEntityId = hireId,
            Amount = amount,
            AmountCents = (long)decimal.Round(amount * 100m, 0),
            Currency = currency,
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// El SearchHireBuilder fija CreatedAt = UtcNow; el agregador bucketea por trimestre
    /// según hire.CreatedAt, así que hay que fijarlo explícitamente al trimestre objetivo.
    /// </summary>
    private static async Task SetHireCreatedAtAsync(AppDbContext db, int hireId, DateTime createdAt)
    {
        var hire = await db.SearchHires.SingleAsync(h => h.Id == hireId);
        hire.CreatedAt = createdAt;
        await db.SaveChangesAsync();
    }

    [Fact(DisplayName = "DAC7-01 · refunds restados del bruto + conversión GBP→EUR a la tasa del hire")]
    public async Task Aggregates_subtract_refunds_and_convert_foreign_currency_to_eur()
    {
        const int year = 2026;
        // Q1 conocido (febrero) en UTC para evitar ambigüedad de bucketing.
        var q1Date = new DateTime(year, 2, 15, 12, 0, 0, DateTimeKind.Utc);

        int expertProfileId;
        int hireAId, hireBId;

        await using (var db = NewDbContext())
        {
            // Seed del chain: client + expert + ExpertProfile + SearchService(ExpertProfileId).
            var client = await new UserBuilder("dac7-01-cli@test.dev").AsClient().Verified().PersistAsync(db);
            var expertUser = await new UserBuilder("dac7-01-exp@test.dev").AsExpert().Verified().PersistAsync(db);
            var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
            expertProfileId = expert.Id;
            var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").PersistAsync(db);

            // Hire A: 100 EUR, finalizado. PlatformFee=10 EUR, Refund=40 EUR.
            var hireA = await new SearchHireBuilder()
                .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
                .WithStatusValue(FinalizationStatusValue)
                .WithAmount(100m, "EUR")
                .PersistAsync(db);
            hireAId = hireA.Id;
            await SetHireCreatedAtAsync(db, hireAId, q1Date);
            await AddFinancialTransactionAsync(db, hireAId, client.Id, "PlatformFee", 10m, "EUR", q1Date);
            await AddFinancialTransactionAsync(db, hireAId, client.Id, "Refund", 40m, "EUR", q1Date);

            // Hire B: 100 GBP, finalizado. Sin reembolso. PlatformFee=5 GBP (misma divisa del hire).
            var hireB = await new SearchHireBuilder()
                .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
                .WithStatusValue(FinalizationStatusValue)
                .WithAmount(100m, "GBP")
                .PersistAsync(db);
            hireBId = hireB.Id;
            await SetHireCreatedAtAsync(db, hireBId, q1Date);
            await AddFinancialTransactionAsync(db, hireBId, client.Id, "PlatformFee", 5m, "GBP", q1Date);
        }

        var exchangeMock = BuildExchangeRateMock();
        var loggingMock = BuildLoggingMock();

        int generated;
        await using (var db = NewDbContext())
        {
            var sut = new Dac7AnnualReportService(db, loggingMock.Object, exchangeMock.Object);
            generated = await sut.GenerateForYearAsync(year, CancellationToken.None);
        }

        generated.Should().Be(1, "solo hay un experto con hires finalizados en el año");

        // Releer desde un DbContext NUEVO.
        await using (var db = NewDbContext())
        {
            var snap = await db.Dac7SellerSnapshots
                .SingleAsync(s => s.ExpertProfileId == expertProfileId && s.Year == year);

            // GrossSalesQ1: hireA neto = 100 - 40 = 60 EUR; hireB = 100 GBP * 2.0 = 200 EUR → 260.
            snap.GrossSalesQ1.Should().Be(260m,
                "hireA (100-40 EUR) + hireB (100 GBP × 2.0) = 60 + 200");

            // PlatformFeesQ1: 10 EUR (hireA) + 5 GBP × 2.0 = 10 EUR (hireB) → 20.
            snap.PlatformFeesQ1.Should().Be(20m,
                "fee hireA 10 EUR + fee hireB 5 GBP × 2.0 = 10 + 10");

            // El reembolso NO reduce el conteo de transacciones: 2 hires → 2.
            snap.TransactionCountQ1.Should().Be(2, "el reembolso no descuenta del conteo");

            // El resto de trimestres a 0.
            snap.GrossSalesQ2.Should().Be(0m);
            snap.GrossSalesQ3.Should().Be(0m);
            snap.GrossSalesQ4.Should().Be(0m);
            snap.PlatformFeesQ2.Should().Be(0m);
            snap.PlatformFeesQ3.Should().Be(0m);
            snap.PlatformFeesQ4.Should().Be(0m);
            snap.TransactionCountQ2.Should().Be(0);
            snap.TransactionCountQ3.Should().Be(0);
            snap.TransactionCountQ4.Should().Be(0);

            snap.ReportingCurrency.Should().Be("EUR");
        }

        // El rate de GBP→EUR se consultó al menos una vez (la conversión ocurrió).
        exchangeMock.Verify(
            s => s.GetRateAsync("GBP", "EUR", It.IsAny<DateTime?>()), Times.AtLeastOnce());
    }

    [Fact(DisplayName = "DAC7-02 · netting EUR puro: bruto neto de refund, sin tocar conversión")]
    public async Task Aggregates_net_refund_in_eur_only()
    {
        const int year = 2026;
        var q1Date = new DateTime(year, 3, 10, 9, 0, 0, DateTimeKind.Utc);

        int expertProfileId;

        await using (var db = NewDbContext())
        {
            var client = await new UserBuilder("dac7-02-cli@test.dev").AsClient().Verified().PersistAsync(db);
            var expertUser = await new UserBuilder("dac7-02-exp@test.dev").AsExpert().Verified().PersistAsync(db);
            var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
            expertProfileId = expert.Id;
            var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").PersistAsync(db);

            var hire = await new SearchHireBuilder()
                .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
                .WithStatusValue(FinalizationStatusValue)
                .WithAmount(200m, "EUR")
                .PersistAsync(db);
            await SetHireCreatedAtAsync(db, hire.Id, q1Date);
            await AddFinancialTransactionAsync(db, hire.Id, client.Id, "PlatformFee", 20m, "EUR", q1Date);
            await AddFinancialTransactionAsync(db, hire.Id, client.Id, "Chargeback", 50m, "EUR", q1Date);
        }

        var exchangeMock = BuildExchangeRateMock();
        var loggingMock = BuildLoggingMock();

        await using (var db = NewDbContext())
        {
            var sut = new Dac7AnnualReportService(db, loggingMock.Object, exchangeMock.Object);
            await sut.GenerateForYearAsync(year, CancellationToken.None);
        }

        await using (var db = NewDbContext())
        {
            var snap = await db.Dac7SellerSnapshots
                .SingleAsync(s => s.ExpertProfileId == expertProfileId && s.Year == year);

            // 200 - 50 (chargeback) = 150 EUR; fee 20 EUR; 1 transacción.
            snap.GrossSalesQ1.Should().Be(150m, "200 bruto - 50 chargeback");
            snap.PlatformFeesQ1.Should().Be(20m);
            snap.TransactionCountQ1.Should().Be(1);
        }
    }
}
