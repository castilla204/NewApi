using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using newApi.Configuration;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;

namespace NewApi.Tests.Integration;

/// <summary>
/// OSS · umbral €10.000 cross-border B2C UE (OssThresholdService.GetCurrentYearReportAsync).
///
/// Valida el fix de captura diferida (modo vendedor): un SearchHire con
/// CaptureStatus="Authorized" tiene el PaymentIntent sólo AUTORIZADO (requires_capture,
/// NO cobrado) hasta que el vendedor confirma la cita (≤48h). Durante esa ventana el
/// dinero NO se ha recaudado, así que NO debe contar para el umbral OSS — si contara,
/// podría disparar la alerta fiscal de forma prematura.
///
/// Reglas verificadas:
///   - "Authorized"  → NO cuenta (no cobrado todavía).
///   - "Failed"      → NO cuenta (captura cancelada/fallida → no cobrado).
///   - "Captured"    → SÍ cuenta (cobro confirmado; modo self captura al instante).
///   - null (legacy) → SÍ cuenta (REGRESIÓN: hires antiguos cobrados tienen el campo
///                     null; excluirlos INFRA-reportaría el umbral = riesgo fiscal peor).
///   - "Pending"     → SÍ cuenta (captura en vuelo que normalmente acaba "Captured";
///                     conservador contra la infra-notificación).
///
/// Patrón reusado de Dac7AnnualReportTests: se siembra el chain real con builders,
/// se ejecuta contra el Postgres del testcontainer y se relee desde el SUT con un
/// DbContext nuevo. CaptureStatus/ClientVat* no los cubre el builder → se fijan a mano
/// tras persistir (como Dac7 hace con CreatedAt).
/// </summary>
public class OssThresholdCaptureStatusTests : IntegrationTestBase
{
    public OssThresholdCaptureStatusTests(PostgresContainerFixture fixture) : base(fixture) { }

    // Estado canónico de finalización del seed (no afecta al cálculo OSS, que filtra por
    // fecha/registro de IVA/CaptureStatus, pero se usa un estado realista).
    private const string FinalizationStatusValue = "completed";

    // País de la plataforma según el perfil fiscal por defecto que inyectamos en el SUT.
    private const string PlatformCountry = "ES";
    // País UE del cliente, cross-border respecto a ES → cuenta para OSS si está cobrado.
    private const string ClientUeCountry = "FR";

    /// <summary>
    /// Perfil fiscal de test: plataforma en ES, umbral 10.000€. IsVatRegistered no afecta
    /// al total acumulado (TotalCrossBorderB2cEur), que es lo que asertamos.
    /// </summary>
    private static IOptions<PlatformFiscalProfile> BuildFiscalProfile()
    {
        return Options.Create(new PlatformFiscalProfile
        {
            IsVatRegistered = true,
            OssRegistered = false,
            OssThresholdEur = 10000m,
            OssThresholdWarningPercent = 80,
            FiscalAddress = new FiscalAddressOptions { Country = PlatformCountry },
        });
    }

    private static Mock<ILoggingService> BuildLoggingMock() => new Mock<ILoggingService>();

    /// <summary>
    /// Fija los campos OSS-relevantes que el SearchHireBuilder no expone: el país de IVA
    /// del cliente (cross-border) y el CaptureStatus. ClientVatNumber se deja null (B2C).
    /// </summary>
    private static async Task SetHireOssFieldsAsync(
        AppDbContext db, int hireId, string clientVatCountryCode, string? captureStatus)
    {
        var hire = await db.SearchHires.SingleAsync(h => h.Id == hireId);
        hire.ClientVatCountryCode = clientVatCountryCode;
        hire.ClientVatNumber = null; // B2C (sin NIF B2B)
        hire.CaptureStatus = captureStatus;
        await db.SaveChangesAsync();
    }

    private static async Task<int> SeedCrossBorderHireAsync(
        AppDbContext db, int clientId, int expertUserId, int serviceId,
        decimal amount, string? captureStatus)
    {
        var hire = await new SearchHireBuilder()
            .ForClient(clientId).ForExpert(expertUserId).ForService(serviceId)
            .WithStatusValue(FinalizationStatusValue)
            .WithAmount(amount, "EUR")
            .PersistAsync(db);
        await SetHireOssFieldsAsync(db, hire.Id, ClientUeCountry, captureStatus);
        return hire.Id;
    }

    private async Task<OssThresholdService.OssThresholdReport> RunReportAsync()
    {
        await using var db = NewDbContext();
        var sut = new OssThresholdService(
            db,
            BuildFiscalProfile(),
            BuildLoggingMock().Object,
            NullLogger<OssThresholdService>.Instance);
        return await sut.GetCurrentYearReportAsync(CancellationToken.None);
    }

    // ── Test A: un hire "Authorized" (cobro diferido pendiente) NO cuenta para el umbral.
    [Fact(DisplayName = "OSS-01 · CaptureStatus=Authorized (no cobrado) NO cuenta para el umbral OSS")]
    public async Task Authorized_hire_is_excluded_from_oss_total()
    {
        await using (var db = NewDbContext())
        {
            var client = await new UserBuilder("oss-01-cli@test.dev").AsClient().Verified().PersistAsync(db);
            var expertUser = await new UserBuilder("oss-01-exp@test.dev").AsExpert().Verified().PersistAsync(db);
            var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
            var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").PersistAsync(db);

            // Único hire del año: 5.000€ cross-border UE pero sólo AUTORIZADO (no cobrado).
            await SeedCrossBorderHireAsync(db, client.Id, expertUser.Id, svc.Id, 5000m, "Authorized");
        }

        var report = await RunReportAsync();

        report.TotalCrossBorderB2cEur.Should().Be(0m,
            "el hire Authorized aún no está cobrado → no debe contar para el umbral OSS");
        report.ThresholdExceeded.Should().BeFalse();
    }

    // ── Test B: hires "Captured" y null (legacy) SÍ cuentan (regresión: no infra-reportar).
    [Fact(DisplayName = "OSS-02 · CaptureStatus=Captured y =NULL (legacy) SÍ cuentan; Authorized se excluye del mix")]
    public async Task Captured_and_legacy_null_hires_count_while_authorized_excluded()
    {
        await using (var db = NewDbContext())
        {
            var client = await new UserBuilder("oss-02-cli@test.dev").AsClient().Verified().PersistAsync(db);
            var expertUser = await new UserBuilder("oss-02-exp@test.dev").AsExpert().Verified().PersistAsync(db);
            var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
            var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").PersistAsync(db);

            await SeedCrossBorderHireAsync(db, client.Id, expertUser.Id, svc.Id, 3000m, "Captured");      // cobrado
            await SeedCrossBorderHireAsync(db, client.Id, expertUser.Id, svc.Id, 4000m, null);            // legacy cobrado
            await SeedCrossBorderHireAsync(db, client.Id, expertUser.Id, svc.Id, 9999m, "Authorized");    // NO cobrado
        }

        var report = await RunReportAsync();

        report.TotalCrossBorderB2cEur.Should().Be(7000m,
            "Captured 3000 + legacy null 4000 = 7000; el Authorized 9999 NO cuenta (si contara, " +
            "el total sería 16999 y dispararía el umbral de forma prematura)");
        report.ThresholdExceeded.Should().BeFalse("7000 < 10000");

        var fr = report.PerCountry.Single(p => p.CountryCode == ClientUeCountry);
        fr.HireCount.Should().Be(2, "sólo los dos hires cobrados (Captured + null) entran en el conteo");
    }

    // ── Test C: un hire "Failed" (captura cancelada/fallida) NO cuenta.
    [Fact(DisplayName = "OSS-03 · CaptureStatus=Failed (captura fallida) NO cuenta para el umbral OSS")]
    public async Task Failed_hire_is_excluded_from_oss_total()
    {
        await using (var db = NewDbContext())
        {
            var client = await new UserBuilder("oss-03-cli@test.dev").AsClient().Verified().PersistAsync(db);
            var expertUser = await new UserBuilder("oss-03-exp@test.dev").AsExpert().Verified().PersistAsync(db);
            var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
            var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").PersistAsync(db);

            await SeedCrossBorderHireAsync(db, client.Id, expertUser.Id, svc.Id, 2000m, "Captured"); // cobrado
            await SeedCrossBorderHireAsync(db, client.Id, expertUser.Id, svc.Id, 6000m, "Failed");   // NO cobrado
        }

        var report = await RunReportAsync();

        report.TotalCrossBorderB2cEur.Should().Be(2000m,
            "sólo el hire Captured (2000) cuenta; el Failed (6000) no se cobró");
    }

    // ── Regresión adicional: "Pending" (captura en vuelo) SÍ cuenta (conservador).
    [Fact(DisplayName = "OSS-04 · CaptureStatus=Pending SÍ cuenta (conservador contra infra-notificación)")]
    public async Task Pending_hire_counts()
    {
        await using (var db = NewDbContext())
        {
            var client = await new UserBuilder("oss-04-cli@test.dev").AsClient().Verified().PersistAsync(db);
            var expertUser = await new UserBuilder("oss-04-exp@test.dev").AsExpert().Verified().PersistAsync(db);
            var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
            var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").PersistAsync(db);

            await SeedCrossBorderHireAsync(db, client.Id, expertUser.Id, svc.Id, 1500m, "Pending");
        }

        var report = await RunReportAsync();

        report.TotalCrossBorderB2cEur.Should().Be(1500m,
            "Pending es captura en vuelo que normalmente acaba Captured → contar es conservador");
    }
}
