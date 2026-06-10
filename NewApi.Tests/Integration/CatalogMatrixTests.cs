using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;

namespace NewApi.Tests.Integration;

/// <summary>
/// Segunda tanda de tests del catálogo (Fase 3b extendida).
///
/// Cubre la matriz completa de splits de dinero, los invariantes de
/// finalización de 1ª/2ª cancelación, la integridad referencial del seed,
/// y los unique indexes anti-doble-cobro sobre FinancialTransaction (P2-3 FIX).
///
/// Estos tests son la red de seguridad estructural: cualquier regresión que
/// rompa la fuente de verdad del marketplace (SEED_ESTADOS_COMPLETO.sql +
/// los unique indexes de FT) revienta uno o más de ellos.
/// </summary>
public class CatalogMatrixTests : IntegrationTestBase
{
    public CatalogMatrixTests(PostgresContainerFixture fixture) : base(fixture) { }

    // ─────────────────────────────────────────────────────────────────────────
    // Matriz de splits: TODOS los StatusConfigurations del catálogo
    // (verificados contra prod vía MCP Postgres durante el desarrollo)
    // ─────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> SplitMatrix => new[]
    {
        // SearchHireStatus
        new object[] { "SearchHireStatus", "completed",                              0m, 95m, 5m, "MONEY-001 flujo feliz" },
        new object[] { "SearchHireStatus", "cancelled",                            100m,  0m, 0m, "MONEY-002 cancel genérico → reembolso completo" },
        new object[] { "SearchHireStatus", "dispute_resolved_client",               90m,  8m, 2m, "MONEY-007 DISP-007: experto recibe 8% por intentar" },
        new object[] { "SearchHireStatus", "dispute_resolved_expert",                0m, 95m, 5m, "MONEY-008 DISP-008" },
        new object[] { "SearchHireStatus", "cancelled_by_client_no_proposal",        0m,100m, 0m, "MONEY-010 cliente no propuso → experto 100%" },
        new object[] { "SearchHireStatus", "cancelled_by_expert_no_response",      100m,  0m, 0m, "MONEY-009 / APPT-013" },
        new object[] { "SearchHireStatus", "cancelled_by_expert_no_report",         95m,  0m, 5m, "MONEY-005 experto no entregó reporte" },

        // AppointmentStatus
        new object[] { "AppointmentStatus", "appointment_cancelled_by_client_no_proposal",        0m,100m, 0m, "APPT-012" },
        new object[] { "AppointmentStatus", "appointment_cancelled_by_client_second",             0m, 95m, 5m, "APPT-006 cliente penalizado 2ª vez" },
        new object[] { "AppointmentStatus", "appointment_cancelled_by_expert_second",           100m,  0m, 0m, "APPT-008 experto penalizado 2ª vez" },
        new object[] { "AppointmentStatus", "appointment_cancelled_by_no_report",                95m,  0m, 5m, "APPT-014 timer expert-report expira" },
        new object[] { "AppointmentStatus", "appointment_completed_without_client_approval",       0m, 95m, 5m, "APPT-015 timer cliente no aprueba → auto" },
    };

    [Theory(DisplayName = "MATRIX · Split de dinero exacto por StatusValue (suma=100, IsActive=true)")]
    [MemberData(nameof(SplitMatrix))]
    public async Task Status_configuration_matches_catalog(
        string statusType, string statusValue,
        decimal expectedClient, decimal expectedExpert, decimal expectedPlatform,
        string catalogRef)
    {
        await using var db = NewDbContext();

        var status = await db.SystemStatuses.SingleOrDefaultAsync(s =>
            s.StatusType == statusType && s.StatusValue == statusValue);
        status.Should().NotBeNull($"el catálogo {catalogRef} promete que existe");

        var config = await db.StatusConfigurations
            .Where(c => c.StatusId == status!.Id &&
                        c.CategoryId == null &&
                        c.ServiceTypeCategoryId == null)
            .SingleOrDefaultAsync();
        config.Should().NotBeNull($"el seed debe traer la política de dinero para {statusValue}");

        config!.ClientPercentage.Should().Be(expectedClient, $"{catalogRef}");
        config.ExpertPercentage.Should().Be(expectedExpert, $"{catalogRef}");
        config.PlatformPercentage.Should().Be(expectedPlatform, $"{catalogRef}");
        config.IsActive.Should().BeTrue();
        config.IsValid.Should().BeTrue("client+expert+platform debe sumar 100");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Invariantes 1ª vs 2ª cancelación (las 4 combinaciones cliente/experto)
    // ─────────────────────────────────────────────────────────────────────────

    [Theory(DisplayName = "INVARIANT · 1ª cancelación NO finaliza · 2ª SÍ finaliza")]
    [InlineData("appointment_cancelled_by_client",        false, "1ª cliente: permite reagendar")]
    [InlineData("appointment_cancelled_by_client_second", true,  "2ª cliente: penaliza (MUD-AI)")]
    [InlineData("appointment_cancelled_by_expert",        false, "1ª experto: permite reagendar")]
    [InlineData("appointment_cancelled_by_expert_second", true,  "2ª experto: penaliza al experto")]
    public async Task First_vs_second_cancellation_finalization_flag(
        string statusValue, bool shouldFinalize, string reason)
    {
        await using var db = NewDbContext();

        var status = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == statusValue);

        status.IsFinalizationStatus.Should().Be(shouldFinalize,
            $"{reason} — si esto cambia, revisa MUD-AI en el FLOWS_CATALOG");
    }

    [Fact(DisplayName = "INVARIANT · Mapping 1ª cancelación → cancelled está INACTIVO (no propaga)")]
    public async Task First_cancellation_mapping_is_inactive()
    {
        await using var db = NewDbContext();
        var cancelledHire = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" && s.StatusValue == "cancelled");

        foreach (var firstCancel in new[] { "appointment_cancelled_by_client",
                                            "appointment_cancelled_by_expert" })
        {
            var status = await db.SystemStatuses.SingleAsync(s =>
                s.StatusType == "AppointmentStatus" && s.StatusValue == firstCancel);
            var mapping = await db.StatusMappings.SingleOrDefaultAsync(m =>
                m.SourceStatusId == status.Id && m.TargetStatusId == cancelledHire.Id);

            mapping.Should().NotBeNull(
                $"el seed debe declarar el mapping de {firstCancel} a cancelled (aunque sea inactivo)");
            mapping!.IsActive.Should().BeFalse(
                $"{firstCancel} NO debe propagar al hire — permite reagendar");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Integridad referencial del seed
    // ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "SEED · Todo StatusConfiguration apunta a un SystemStatus existente y suma 100")]
    public async Task All_status_configurations_reference_valid_status_and_sum_100()
    {
        await using var db = NewDbContext();

        var configs = await db.StatusConfigurations
            .Where(c => c.CategoryId == null && c.ServiceTypeCategoryId == null)
            .Include(c => c.Status)
            .ToListAsync();

        configs.Should().NotBeEmpty("el seed debe traer al menos las 12+ configuraciones del catálogo");

        foreach (var cfg in configs)
        {
            cfg.Status.Should().NotBeNull($"orphan StatusConfiguration #{cfg.Id}");

            var sum = cfg.ClientPercentage + cfg.ExpertPercentage + cfg.PlatformPercentage;
            sum.Should().Be(100m,
                $"config de {cfg.Status.StatusValue} suma {sum}, no 100");
        }
    }

    [Fact(DisplayName = "SEED · StatusMappings apuntan a SystemStatuses existentes; no ciclos triviales")]
    public async Task Status_mappings_are_consistent()
    {
        await using var db = NewDbContext();

        var mappings = await db.StatusMappings
            .Include(m => m.SourceStatus)
            .Include(m => m.TargetStatus)
            .ToListAsync();

        mappings.Should().NotBeEmpty();

        foreach (var m in mappings)
        {
            m.SourceStatus.Should().NotBeNull($"mapping #{m.Id} tiene SourceStatusId huérfano");
            m.TargetStatus.Should().NotBeNull($"mapping #{m.Id} tiene TargetStatusId huérfano");

            // El catálogo dice que los mappings son AppointmentStatus → SearchHireStatus
            m.SourceStatus.StatusType.Should().Be("AppointmentStatus");
            m.TargetStatus.StatusType.Should().Be("SearchHireStatus");

            m.SourceStatusId.Should().NotBe(m.TargetStatusId,
                "ningún mapping debería ser self-loop");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FK SearchHire → SystemStatus (cualquier hire creado debe poder referenciar
    // un status válido)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "FK · SearchHireBuilder no acepta StatusValue inexistente")]
    public async Task SearchHire_with_invalid_status_throws()
    {
        await using var db = NewDbContext();
        var clientUser = await new UserBuilder("fk@test.dev").AsClient().PersistAsync(db);
        var expertUser = await new UserBuilder("fk-expert@test.dev").AsExpert().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).PersistAsync(db);

        var act = async () => await new SearchHireBuilder()
            .ForClient(clientUser.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .WithStatusValue("estado_inventado_que_no_existe")
            .PersistAsync(db);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "el builder debe rechazar StatusValues que no estén en el seed " +
            "antes de pegarle a la BD (mejor diagnóstico para tests)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Unique indexes anti-doble-cobro sobre FinancialTransaction (P2-3 FIX)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "FT · Unique StripeRefundId previene doble refund")]
    public async Task FinancialTransaction_unique_StripeRefundId()
    {
        await using var db = NewDbContext();
        var client = await new UserBuilder("ft1@test.dev").AsClient().PersistAsync(db);

        var refundId = "re_test_" + Guid.NewGuid().ToString("N");

        db.FinancialTransactions.Add(new FinancialTransaction
        {
            UserId = client.Id,
            Amount = 50m, AmountCents = 5000, Currency = "EUR",
            TransactionType = "Refund",
            StripeRefundId = refundId,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await using var db2 = NewDbContext();
        db2.FinancialTransactions.Add(new FinancialTransaction
        {
            UserId = client.Id,
            Amount = 50m, AmountCents = 5000, Currency = "EUR",
            TransactionType = "Refund",
            StripeRefundId = refundId, // ← duplicado
            CreatedAt = DateTime.UtcNow,
        });

        var act = async () => await db2.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "IX_FT_StripeRefundId_uq garantiza idempotencia ante reintentos del webhook charge.refunded");
    }

    [Fact(DisplayName = "FT · Unique ServicePayment (RelatedEntityType, RelatedEntityId) previene doble pago de hire")]
    public async Task FinancialTransaction_unique_service_payment_per_hire()
    {
        await using var db = NewDbContext();
        var client = await new UserBuilder("ft2@test.dev").AsClient().PersistAsync(db);
        var expertUser = await new UserBuilder("ft2-expert@test.dev").AsExpert().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).PersistAsync(db);
        var hire = await new SearchHireBuilder()
            .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .WithAmount(100m).PersistAsync(db);

        db.FinancialTransactions.Add(new FinancialTransaction
        {
            UserId = client.Id,
            Amount = 100m, AmountCents = 10000, Currency = "EUR",
            TransactionType = "ServicePayment",
            RelatedEntityType = "SearchHire",
            RelatedEntityId = hire.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await using var db2 = NewDbContext();
        db2.FinancialTransactions.Add(new FinancialTransaction
        {
            UserId = client.Id,
            Amount = 100m, AmountCents = 10000, Currency = "EUR",
            TransactionType = "ServicePayment", // ← mismo tipo
            RelatedEntityType = "SearchHire",
            RelatedEntityId = hire.Id, // ← mismo hire
            CreatedAt = DateTime.UtcNow,
        });

        var act = async () => await db2.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "el unique index parcial sobre (RelatedEntityType, RelatedEntityId) WHERE TransactionType='ServicePayment' " +
            "impide que un hire reciba dos pagos por error en re-procesamiento de webhook");
    }

    [Fact(DisplayName = "FT · Refund con StripeRefundId distinto sobre el mismo hire SÍ está permitido (no es ServicePayment)")]
    public async Task Multiple_refunds_on_same_hire_are_allowed()
    {
        // Verifica que el unique index de ServicePayment NO bloquea otros tipos
        // de FT que apuntan al mismo hire. Importante: un hire puede tener
        // 1 ServicePayment + 1 Refund + 1 Payout sin colisionar.
        await using var db = NewDbContext();
        var client = await new UserBuilder("ft3@test.dev").AsClient().PersistAsync(db);
        var expertUser = await new UserBuilder("ft3-expert@test.dev").AsExpert().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).PersistAsync(db);
        var hire = await new SearchHireBuilder()
            .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .WithAmount(100m).PersistAsync(db);

        db.FinancialTransactions.AddRange(
            new FinancialTransaction
            {
                UserId = client.Id, Amount = 100m, AmountCents = 10000, Currency = "EUR",
                TransactionType = "ServicePayment",
                RelatedEntityType = "SearchHire", RelatedEntityId = hire.Id,
                CreatedAt = DateTime.UtcNow,
            },
            new FinancialTransaction
            {
                UserId = client.Id, Amount = 100m, AmountCents = 10000, Currency = "EUR",
                TransactionType = "Refund",
                RelatedEntityType = "SearchHire", RelatedEntityId = hire.Id,
                StripeRefundId = "re_test_" + Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
            },
            new FinancialTransaction
            {
                UserId = expertUser.Id, Amount = 95m, AmountCents = 9500, Currency = "EUR",
                TransactionType = "Payout",
                RelatedEntityType = "SearchHire", RelatedEntityId = hire.Id,
                StripeTransferId = "tr_test_" + Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
            }
        );

        var act = async () => await db.SaveChangesAsync();
        await act.Should().NotThrowAsync(
            "el unique index es parcial: solo afecta a TransactionType='ServicePayment'. " +
            "Refund + Payout sobre el mismo hire deben coexistir.");
    }
}
