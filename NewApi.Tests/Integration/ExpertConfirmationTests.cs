using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using FluentAssertions;

namespace NewApi.Tests.Integration;

/// <summary>
/// Confirmación del experto (Tarea 4 — núcleo de pagos).
///
/// La cita nace en `appointment_pending_expert_confirmation` con el pago AUTORIZADO
/// (PI en requires_capture, hire.CaptureStatus="Authorized"). El experto decide vía
/// su magic link:
///
///   • POST /api/expert-confirmation/{token}/approve → confirma la cita y CAPTURA el pago
///     (POST .../capture → succeeded), CaptureStatus="Captured", token anulado.
///   • POST /api/expert-confirmation/{token}/reject  → cancela la AUTORIZACIÓN (0 €, sin
///     POST capture), reembolso 100% al cliente, hire 'cancelled' y strike al experto.
///
/// Reusa el harness HTTP real (ApiFactoryFixture + FakeStripeServer) y siembra un hire con
/// Appointment pending_expert_confirmation + ExpertConfirmationToken + FT ServicePayment
/// con un PI único en requires_capture. Como Hangfire no procesa jobs en tests
/// (HANGFIRE_SERVER_DISABLED=1), el reject drena la distribución de dinero llamando
/// ProcessMoneyDistributionAsync directamente (mismo patrón que HttpHireFlowTests).
/// </summary>
[Collection("Api")]
public class ExpertConfirmationTests
{
    private readonly ApiFactoryFixture _api;
    public ExpertConfirmationTests(ApiFactoryFixture api) => _api = api;

    // Siembra un hire con Appointment en pending_expert_confirmation + ExpertConfirmationToken
    // + ExpertConfirmationDeadline + FT ServicePayment (PI único, requires_capture) + Authorized.
    // withServicePayment=false omite la FT ServicePayment para ejercitar la rama "sin PaymentIntent".
    private async Task<(string token, int serviceId, int hireId, int expertUserId, string pi)> SeedPendingExpertConfirmationAsync(
        bool withServicePayment = true)
    {
        await using var db = _api.CreateDbContext();
        var expertUser = await new UserBuilder().AsExpert().Verified().PersistAsync(db);
        var expert = await new ExpertProfileBuilder(expertUser.Id).Approved().PersistAsync(db);
        var svc = await new SearchServiceBuilder(expert.Id).WithPrice(100m, "EUR").WithDuration(1).PersistAsync(db);

        var client = await new UserBuilder().AsClient().Verified().PersistAsync(db);
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var hire = await new SearchHireBuilder()
            .ForClient(client.Id).ForExpert(expertUser.Id).ForService(svc.Id)
            .WithStatusValue("pending").PersistAsync(db);

        hire.CreatedAt = DateTime.UtcNow;
        hire.ExpertConfirmationToken = token;
        hire.ExpertConfirmationDeadline = DateTime.UtcNow.AddHours(36);
        hire.ExpertTimezone = "Europe/Madrid";
        hire.CaptureStatus = "Authorized"; // checkout difirió la captura

        var startsAt = DateTime.UtcNow.AddDays(4);
        var pendingStatus = await db.SystemStatuses.SingleAsync(
            s => s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_pending_expert_confirmation");
        db.Appointments.Add(new Appointment
        {
            SearchHireId = hire.Id,
            StatusId = pendingStatus.Id,
            ExpertId = hire.ExpertId,
            StartsAtUtc = startsAt,
            EndsAtUtc = startsAt.AddHours(1),
            BlocksCalendar = true,
            Location = "Calle Mayor 1",
            DoorNumber = "3B",
            SiteDetails = "Portal azul",
            Latitude = 40.0m,
            Longitude = -3.7m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var pi = "pi_ec_" + Guid.NewGuid().ToString("N")[..12];
        if (withServicePayment)
        {
            db.FinancialTransactions.Add(new FinancialTransaction
            {
                UserId = client.Id,
                Amount = 100m,
                AmountCents = 10000,
                Currency = "EUR",
                TransactionType = "ServicePayment",
                RelatedEntityType = "SearchHire",
                RelatedEntityId = hire.Id,
                StripePaymentIntentId = pi,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return (token, svc.Id, hire.Id, expertUser.Id, pi);
    }

    // ── EC-01: GET contexto → 200 con datos de la cita (sin datos del cliente).
    [Fact(DisplayName = "EC-01 · GET contexto → 200 con datos de la cita, sin datos del cliente")]
    public async Task GetContext_returns_appointment_data_without_client_pii()
    {
        var (token, serviceId, _, _, _) = await SeedPendingExpertConfirmationAsync();

        var res = await _api.Client.GetAsync($"/api/expert-confirmation/{token}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("serviceId");
        body.Should().Contain("startsAtUtc");
        body.Should().Contain("Calle Mayor 1");
        body.Should().Contain("deadline");
        body.Should().Contain("expired");
        // No exponer datos personales del cliente.
        body.ToLowerInvariant().Should().NotContain("email");
        body.ToLowerInvariant().Should().NotContain("phone");
        body.Should().NotContain("clientId", "el contexto del experto no expone identidad del cliente");
    }

    // ── EC-02: approve → PI succeeded (hay POST .../capture), cita appointment_confirmed,
    //    CaptureStatus="Captured", token anulado.
    [Fact(DisplayName = "EC-02 · approve → captura (PI succeeded), cita confirmada, Captured, token null")]
    public async Task Approve_captures_and_confirms_appointment()
    {
        var (token, _, hireId, _, pi) = await SeedPendingExpertConfirmationAsync();

        var res = await _api.Client.PostAsync($"/api/expert-confirmation/{token}/approve", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        _api.FakeStripe.Requests.Should().Contain(r => r == $"POST /v1/payment_intents/{pi}/capture",
            "approve captura el pago del comprador");

        await using var db = _api.CreateDbContext();
        var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
        hire.CaptureStatus.Should().Be("Captured");
        hire.ExpertConfirmationToken.Should().BeNull("token de un solo uso");

        var appt = await db.Appointments.AsNoTracking().Include(a => a.Status)
            .SingleAsync(a => a.SearchHireId == hireId);
        appt.Status!.StatusValue.Should().Be("appointment_confirmed");
    }

    // ── EC-03: approve con captura fallida → 402; sigue pending_expert_confirmation,
    //    CaptureStatus="Failed", token NO null (reintentable).
    [Fact(DisplayName = "EC-03 · approve con captura fallida → 402, sigue pending, Failed, token vivo")]
    public async Task Approve_with_capture_failure_rolls_back_without_confirming()
    {
        var (token, _, hireId, _, pi) = await SeedPendingExpertConfirmationAsync();
        _api.FakeStripe.CaptureFailurePaymentIntents[pi] = 1;
        try
        {
            var res = await _api.Client.PostAsync($"/api/expert-confirmation/{token}/approve", content: null);
            res.StatusCode.Should().Be(HttpStatusCode.PaymentRequired, await res.Content.ReadAsStringAsync());
        }
        finally
        {
            _api.FakeStripe.CaptureFailurePaymentIntents.TryRemove(pi, out _);
        }

        await using var db = _api.CreateDbContext();
        var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
        hire.CaptureStatus.Should().Be("Failed", "la captura falló → marcado Failed en contexto limpio");
        hire.ExpertConfirmationToken.Should().NotBeNullOrEmpty("el rollback deja el token vivo (reintentable)");

        var appt = await db.Appointments.AsNoTracking().Include(a => a.Status)
            .SingleAsync(a => a.SearchHireId == hireId);
        appt.Status!.StatusValue.Should().Be("appointment_pending_expert_confirmation",
            "el rollback deshace la confirmación: la cita sigue pendiente");
    }

    // ── EC-04: reject → PI canceled (0 €, sin POST capture), hire finalizado a 'cancelled',
    //    cita appointment_cancelled_by_expert_strike, strike +1, token null.
    [Fact(DisplayName = "EC-04 · reject → PI cancelado (0€), strike +1, hire cancelled, token null")]
    public async Task Reject_cancels_authorization_adds_strike_refunds_buyer()
    {
        var (token, _, hireId, expertUserId, pi) = await SeedPendingExpertConfirmationAsync();

        var res = await _api.Client.PostAsync($"/api/expert-confirmation/{token}/reject", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        // Mutex + strike síncronos: el token se consume y el strike sube de inmediato.
        await using (var db = _api.CreateDbContext())
        {
            var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
            hire.ExpertConfirmationToken.Should().BeNull("reject anula el token de un solo uso");
            var strikes = await db.ExpertProfiles.AsNoTracking()
                .Where(p => p.UserId == expertUserId).Select(p => p.CancellationStrikes).SingleAsync();
            strikes.Should().Be(1, "rechazar la cita añade un strike de cancelación al experto");
        }

        // Drenar la distribución encolada (Hangfire no corre en tests): mismos argumentos que el endpoint.
        using (var scope = _api.Factory.Services.CreateScope())
        {
            var refundService = scope.ServiceProvider.GetRequiredService<newApi.Services.StripeRefundService>();
            var ok = await refundService.ProcessMoneyDistributionAsync(
                hireId,
                "appointment_cancelled_by_expert_strike",
                "El experto rechazó la cita: devolución íntegra al cliente (0€) + strike.",
                null,
                true);
            ok.Should().BeTrue("la distribución de una cancelación sin cobro debe completar (N10 cancela el PI)");
        }

        // N10: PI en requires_capture → se CANCELA (0 €), nunca se captura.
        _api.FakeStripe.Requests.Should().Contain(r => r == $"POST /v1/payment_intents/{pi}/cancel",
            "un PI no capturado se cancela (0 €)");
        _api.FakeStripe.Requests.Should().NotContain(r => r == $"POST /v1/payment_intents/{pi}/capture",
            "rechazar NUNCA cobra al comprador");

        await using (var db = _api.CreateDbContext())
        {
            // Hire finalizado a 'cancelled'.
            var statusValue = await db.SearchHires.AsNoTracking()
                .Where(h => h.Id == hireId)
                .Join(db.SystemStatuses.AsNoTracking(), h => h.StatusId, s => s.Id, (h, s) => s.StatusValue)
                .SingleAsync();
            statusValue.Should().Be("cancelled", "la devolución 100% al comprador finaliza el hire a 'cancelled'");

            // La cita pasa a appointment_cancelled_by_expert_strike.
            var apptStatus = await db.Appointments.AsNoTracking().Include(a => a.Status)
                .Where(a => a.SearchHireId == hireId).Select(a => a.Status!.StatusValue).SingleAsync();
            apptStatus.Should().Be("appointment_cancelled_by_expert_strike");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tarea 5 — timer "expert_confirmation": auto-cancela (sin strike) si el experto no
    // aprueba/rechaza dentro de su ventana; se CANCELA cuando el experto responde a tiempo.
    // El handler ProcessAppointmentTimerAsync se invoca directamente (Hangfire no corre en
    // tests, HANGFIRE_SERVER_DISABLED=1), igual que JourneyTimerTests/HttpHireFlowTests.
    // ─────────────────────────────────────────────────────────────────────────

    // Siembra una fila AppointmentTimer "expert_confirmation" activa para la cita del hire.
    private async Task<int> SeedExpertConfirmationTimerAsync(int hireId, DateTime endTimeUtc)
    {
        await using var db = _api.CreateDbContext();
        var apptId = await db.Appointments.Where(a => a.SearchHireId == hireId).Select(a => a.Id).SingleAsync();
        var timer = new AppointmentTimer
        {
            AppointmentId = apptId,
            TimerType = "expert_confirmation",
            StartTime = DateTime.UtcNow,
            EndTime = endTimeUtc,
            IsExpired = false,
            CreatedAt = DateTime.UtcNow,
        };
        db.AppointmentTimers.Add(timer);
        await db.SaveChangesAsync();
        return timer.Id;
    }

    // ── EC-T1: timer expira con la cita aún pending → auto-cancel SIN strike: cita
    //    appointment_cancelled_by_expert_no_response, hire 'cancelled', PI cancelado (0€,
    //    sin Refund FT), token anulado, sin strike al experto.
    [Fact(DisplayName = "EC-T1 · timer expert_confirmation expira (pending) → no_response, hire cancelled, PI 0€, sin strike, token null")]
    public async Task ExpertConfirmationTimer_expires_while_pending_auto_cancels_no_strike()
    {
        var (_, _, hireId, expertUserId, pi) = await SeedPendingExpertConfirmationAsync();
        var timerId = await SeedExpertConfirmationTimerAsync(hireId, DateTime.UtcNow.AddSeconds(-1));

        using (var scope = _api.Factory.Services.CreateScope())
        {
            var appointmentService = scope.ServiceProvider.GetRequiredService<IAppointmentService>();
            await appointmentService.ProcessAppointmentTimerAsync(timerId);
        }

        // N10: PI en requires_capture → se CANCELA (0 €), nunca se captura.
        _api.FakeStripe.Requests.Should().Contain(r => r == $"POST /v1/payment_intents/{pi}/cancel",
            "el auto-cancel por no respuesta cancela el PI no capturado (0 €)");
        _api.FakeStripe.Requests.Should().NotContain(r => r == $"POST /v1/payment_intents/{pi}/capture",
            "el auto-cancel NUNCA cobra al comprador");

        await using var db = _api.CreateDbContext();

        var apptStatus = await db.Appointments.AsNoTracking().Include(a => a.Status)
            .Where(a => a.SearchHireId == hireId).Select(a => a.Status!.StatusValue).SingleAsync();
        apptStatus.Should().Be("appointment_cancelled_by_expert_no_response");

        var hireStatus = await db.SearchHires.AsNoTracking()
            .Where(h => h.Id == hireId)
            .Join(db.SystemStatuses.AsNoTracking(), h => h.StatusId, s => s.Id, (h, s) => s.StatusValue)
            .SingleAsync();
        hireStatus.Should().Be("cancelled", "el auto-cancel finaliza el hire");

        var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
        hire.ExpertConfirmationToken.Should().BeNull("el timer anula el token de confirmación");

        // SIN strike: la no respuesta no se penaliza como una cancelación activa.
        var strikes = await db.ExpertProfiles.AsNoTracking()
            .Where(p => p.UserId == expertUserId).Select(p => p.CancellationStrikes).SingleAsync();
        strikes.Should().Be(0, "el auto-cancel por no respuesta NO añade strike");

        // Sin FT Refund: cancelar un PI no capturado no devuelve dinero (no se cobró nada).
        var refundFts = await db.FinancialTransactions.AsNoTracking()
            .Where(t => t.RelatedEntityType == "SearchHire" && t.RelatedEntityId == hireId && t.TransactionType == "Refund")
            .ToListAsync();
        refundFts.Should().BeEmpty("cancelar un PI no capturado no genera FT Refund");

        // Timer expirado.
        var timer = await db.AppointmentTimers.AsNoTracking().SingleAsync(t => t.Id == timerId);
        timer.IsExpired.Should().BeTrue();
        timer.ExpiredAt.Should().NotBeNull();
    }

    // ── EC-T2: timer dispara cuando la cita YA está confirmed (el experto aprobó antes) → no-op:
    //    no cambia estado, no cancela/captura el PI, timer queda expirado (guard de estado).
    [Fact(DisplayName = "EC-T2 · timer expert_confirmation dispara con cita ya confirmed → no-op")]
    public async Task ExpertConfirmationTimer_is_noop_when_already_confirmed()
    {
        var (_, _, hireId, _, pi) = await SeedPendingExpertConfirmationAsync();
        var timerId = await SeedExpertConfirmationTimerAsync(hireId, DateTime.UtcNow.AddSeconds(-1));

        // El experto aprobó antes: cita → appointment_confirmed.
        await using (var db = _api.CreateDbContext())
        {
            var confirmed = await db.SystemStatuses.SingleAsync(
                s => s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_confirmed");
            await db.Appointments.Where(a => a.SearchHireId == hireId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.StatusId, confirmed.Id));
        }

        var requestsBefore = _api.FakeStripe.Requests.Count(r => r.Contains(pi));

        using (var scope = _api.Factory.Services.CreateScope())
        {
            var appointmentService = scope.ServiceProvider.GetRequiredService<IAppointmentService>();
            await appointmentService.ProcessAppointmentTimerAsync(timerId);
        }

        // No-op: el guard de estado salta porque la cita ya no está pending_expert_confirmation.
        _api.FakeStripe.Requests.Count(r => r.Contains(pi)).Should().Be(requestsBefore,
            "no debe tocar el PI: el experto ya confirmó");

        await using var db2 = _api.CreateDbContext();
        var apptStatus = await db2.Appointments.AsNoTracking().Include(a => a.Status)
            .Where(a => a.SearchHireId == hireId).Select(a => a.Status!.StatusValue).SingleAsync();
        apptStatus.Should().Be("appointment_confirmed", "la cita confirmada no cambia");

        var timer = await db2.AppointmentTimers.AsNoTracking().SingleAsync(t => t.Id == timerId);
        timer.IsExpired.Should().BeTrue("el timer se marca expirado aunque sea no-op (guard de estado)");
    }

    // ── EC-T3: approve → el timer expert_confirmation activo queda expirado (cancelado).
    [Fact(DisplayName = "EC-T3 · approve cancela el timer expert_confirmation (IsExpired=true)")]
    public async Task Approve_cancels_expert_confirmation_timer()
    {
        var (token, _, hireId, _, _) = await SeedPendingExpertConfirmationAsync();
        var timerId = await SeedExpertConfirmationTimerAsync(hireId, DateTime.UtcNow.AddHours(12));

        var res = await _api.Client.PostAsync($"/api/expert-confirmation/{token}/approve", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        await using var db = _api.CreateDbContext();
        var timer = await db.AppointmentTimers.AsNoTracking().SingleAsync(t => t.Id == timerId);
        timer.IsExpired.Should().BeTrue("approve cancela el timer de confirmación");
        timer.ExpiredAt.Should().NotBeNull();
        timer.HangfireJobId.Should().BeNull("se limpia la referencia al job al cancelar");
    }

    // ── EC-T4: reject → el timer expert_confirmation activo queda expirado (cancelado).
    [Fact(DisplayName = "EC-T4 · reject cancela el timer expert_confirmation (IsExpired=true)")]
    public async Task Reject_cancels_expert_confirmation_timer()
    {
        var (token, _, hireId, _, _) = await SeedPendingExpertConfirmationAsync();
        var timerId = await SeedExpertConfirmationTimerAsync(hireId, DateTime.UtcNow.AddHours(12));

        var res = await _api.Client.PostAsync($"/api/expert-confirmation/{token}/reject", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        await using var db = _api.CreateDbContext();
        var timer = await db.AppointmentTimers.AsNoTracking().SingleAsync(t => t.Id == timerId);
        timer.IsExpired.Should().BeTrue("reject cancela el timer de confirmación");
        timer.ExpiredAt.Should().NotBeNull();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tarea 7 — WATCHDOG de expiración (red de seguridad del timer expert_confirmation).
    // ProcessExpiredExpertConfirmationsAsync cubre el caso de timer Hangfire perdido: si el
    // plazo (ExpertConfirmationDeadline) venció con la cita aún pending y token vivo, el
    // watchdog reclama (anula el token) y encola la distribución de dinero (reembolso 100%).
    // Como Hangfire no procesa jobs en tests, drenamos la distribución a mano (mismo patrón).
    // ─────────────────────────────────────────────────────────────────────────

    // ── EC-W1: deadline pasado + token vivo + cita pending + SIN timer (timer perdido) →
    //    el watchdog anula el token y la distribución cancela el PI (0€), hire 'cancelled',
    //    cita appointment_cancelled_by_expert_no_response, sin FT Refund.
    [Fact(DisplayName = "EC-W1 · watchdog expira confirmación sin timer → token null, hire cancelled, PI 0€, no_response")]
    public async Task ExpiryWatchdog_expires_confirmation_without_timer()
    {
        // Sembrar y forzar el deadline al pasado (NO sembramos AppointmentTimer → simula timer perdido).
        var (_, _, hireId, _, pi) = await SeedPendingExpertConfirmationAsync();
        await using (var db = _api.CreateDbContext())
        {
            await db.SearchHires.Where(h => h.Id == hireId)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.ExpertConfirmationDeadline, DateTime.UtcNow.AddHours(-1)));
        }

        // Paso 1: el watchdog reclama el hire (anula el token) y encola la distribución.
        using (var scope = _api.Factory.Services.CreateScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IPlatformMaintenanceService>();
            await maintenance.ProcessExpiredExpertConfirmationsAsync();
        }

        await using (var db = _api.CreateDbContext())
        {
            var hire = await db.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
            hire.ExpertConfirmationToken.Should().BeNull("el watchdog anula el token de un solo uso (mutex)");
        }

        // Paso 2: drenar la distribución encolada (mismos argumentos que encola el watchdog).
        using (var scope = _api.Factory.Services.CreateScope())
        {
            var refundService = scope.ServiceProvider.GetRequiredService<newApi.Services.StripeRefundService>();
            var ok = await refundService.ProcessMoneyDistributionAsync(
                hireId,
                "appointment_cancelled_by_expert_no_response",
                "Watchdog: expert confirmation deadline passed (no response).",
                null,
                true);
            ok.Should().BeTrue("la distribución de una cancelación pre-captura completa (N10 cancela el PI)");
        }

        // N10: PI en requires_capture → CANCELADO (0 €), nunca capturado.
        _api.FakeStripe.Requests.Should().Contain(r => r == $"POST /v1/payment_intents/{pi}/cancel",
            "el watchdog cancela el PI no capturado (0 € de fee)");
        _api.FakeStripe.Requests.Should().NotContain(r => r == $"POST /v1/payment_intents/{pi}/capture",
            "expirar la confirmación NUNCA cobra al comprador");

        await using (var db = _api.CreateDbContext())
        {
            var apptStatus = await db.Appointments.AsNoTracking().Include(a => a.Status)
                .Where(a => a.SearchHireId == hireId).Select(a => a.Status!.StatusValue).SingleAsync();
            apptStatus.Should().Be("appointment_cancelled_by_expert_no_response");

            var hireStatus = await db.SearchHires.AsNoTracking()
                .Where(h => h.Id == hireId)
                .Join(db.SystemStatuses.AsNoTracking(), h => h.StatusId, s => s.Id, (h, s) => s.StatusValue)
                .SingleAsync();
            hireStatus.Should().Be("cancelled", "la devolución 100% al comprador finaliza el hire");

            var refundFts = await db.FinancialTransactions.AsNoTracking()
                .Where(t => t.RelatedEntityType == "SearchHire" && t.RelatedEntityId == hireId && t.TransactionType == "Refund")
                .ToListAsync();
            refundFts.Should().BeEmpty("cancelar un PI no capturado no genera FT Refund");
        }
    }

    // ── EC-W2: el watchdog NO toca un hire cuyo token YA fue reclamado (token null) aunque el
    //    deadline esté vencido → idempotencia (no reencola distribución sobre algo ya resuelto).
    [Fact(DisplayName = "EC-W2 · watchdog ignora confirmación ya reclamada (token null)")]
    public async Task ExpiryWatchdog_ignores_already_claimed()
    {
        var (_, _, hireId, _, pi) = await SeedPendingExpertConfirmationAsync();
        await using (var db = _api.CreateDbContext())
        {
            await db.SearchHires.Where(h => h.Id == hireId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.ExpertConfirmationDeadline, DateTime.UtcNow.AddHours(-1))
                    .SetProperty(h => h.ExpertConfirmationToken, (string?)null));
        }

        using (var scope = _api.Factory.Services.CreateScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IPlatformMaintenanceService>();
            await maintenance.ProcessExpiredExpertConfirmationsAsync();
        }

        // No tocó el PI: el token null lo excluye del lote (la query filtra token != null).
        _api.FakeStripe.Requests.Should().NotContain(r => r.StartsWith($"POST /v1/payment_intents/{pi}/"),
            "un hire ya reclamado (token null) está fuera de la query del watchdog");
    }

    // ── EC-INV1: approve → además de capturar, ENCOLA la factura (OBS-1). El job
    //    SendInvoiceByEmailBackgroundJob queda en la cola de Hangfire (server disabled en tests).
    [Fact(DisplayName = "EC-INV1 · approve encola la factura (SendInvoiceByEmailBackgroundJob)")]
    public async Task Approve_enqueues_invoice_job()
    {
        var (token, _, hireId, _, _) = await SeedPendingExpertConfirmationAsync();

        var res = await _api.Client.PostAsync($"/api/expert-confirmation/{token}/approve", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        // El enqueue es best-effort, pero con cliente verificado (tiene email) DEBE encolarse.
        // Inspeccionamos la cola "default" de Hangfire (PostgreSQL storage del testcontainer).
        var monitoring = Hangfire.JobStorage.Current.GetMonitoringApi();
        var enqueued = monitoring.EnqueuedJobs("default", 0, 1000);
        var invoiceJob = enqueued.FirstOrDefault(j => j.Value.Job != null
            && j.Value.Job.Method.Name == "SendInvoiceByEmailBackgroundJob"
            && j.Value.Job.Args.Count > 0
            && Equals(j.Value.Job.Args[0], hireId));
        invoiceJob.Value.Should().NotBeNull(
            "approve debe encolar la factura del hire tras capturar el pago (OBS-1)");
    }

    // ── EC-05: approve con token ya anulado → 404 (idempotencia del token single-use).
    [Fact(DisplayName = "EC-05 · approve con token ya null → 404")]
    public async Task Approve_with_consumed_token_returns_not_found()
    {
        var (token, _, hireId, _, _) = await SeedPendingExpertConfirmationAsync();
        await using (var db = _api.CreateDbContext())
        {
            await db.SearchHires.Where(h => h.Id == hireId)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.ExpertConfirmationToken, (string?)null));
        }

        var res = await _api.Client.PostAsync($"/api/expert-confirmation/{token}/approve", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, await res.Content.ReadAsStringAsync());
    }

    // ── EC-06: approve sobre cita en estado no-pending → 409 (idempotencia del estado).
    [Fact(DisplayName = "EC-06 · approve sobre cita no-pending → 409")]
    public async Task Approve_when_not_pending_returns_conflict()
    {
        var (token, _, hireId, _, _) = await SeedPendingExpertConfirmationAsync();
        await using (var db = _api.CreateDbContext())
        {
            var confirmed = await db.SystemStatuses.SingleAsync(
                s => s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_confirmed");
            await db.Appointments.Where(a => a.SearchHireId == hireId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.StatusId, confirmed.Id));
        }

        var res = await _api.Client.PostAsync($"/api/expert-confirmation/{token}/approve", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync());
    }

    // ── EC-07: reject sobre cita en estado no-pending → 409, SIN strike y SIN consumir el token.
    //    Regresión del FIX [F2-REJECT-GUARD] (auditoría 2026-07-12): antes un reject sobre una
    //    cita ya cancelada (p.ej. por el webhook de PI fallido/cancelado con el token aún vivo)
    //    pasaba limpio → strike injusto + distribución redundante encolada.
    [Fact(DisplayName = "EC-07 · reject sobre cita no-pending → 409, sin strike, token intacto")]
    public async Task Reject_when_not_pending_returns_conflict_without_strike()
    {
        var (token, _, hireId, expertUserId, pi) = await SeedPendingExpertConfirmationAsync();
        await using (var db = _api.CreateDbContext())
        {
            // Simula el desenlace del webhook (HandlePaymentIntentFailed/Canceled): cita cancelada.
            var cancelled = await db.SystemStatuses.SingleAsync(
                s => s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_cancelled_by_client");
            await db.Appointments.Where(a => a.SearchHireId == hireId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.StatusId, cancelled.Id));
        }

        var res = await _api.Client.PostAsync($"/api/expert-confirmation/{token}/reject", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync());

        await using var db2 = _api.CreateDbContext();
        var strikes = await db2.ExpertProfiles.AsNoTracking()
            .Where(p => p.UserId == expertUserId).Select(p => p.CancellationStrikes).SingleAsync();
        strikes.Should().Be(0, "rechazar una cita ya muerta no debe penalizar al experto");

        var hire = await db2.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId);
        hire.ExpertConfirmationToken.Should().NotBeNullOrEmpty(
            "el guard corta ANTES de consumir el token (no hay mutex que tomar sobre una cita muerta)");

        _api.FakeStripe.Requests.Should().NotContain(r => r.StartsWith($"POST /v1/payment_intents/{pi}/"),
            "un reject rechazado por el guard no toca el PI");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tarea 6 — notificaciones (email magic-link + in-app + SMS) + recordatorios.
    // El harness corre INotificationService (encola email en Hangfire, no ejecuta) e
    // IInAppNotificationService (inserta filas Notification reales) contra la BD real.
    // Verificamos los efectos observables (filas Notification) y que SMS sin configurar no peta.
    // ─────────────────────────────────────────────────────────────────────────

    // ── EC-N1: NotifyExpertConfirmationRequestedAsync → in-app al experto con Url que contiene el
    //    token y "/confirmar-cita/". Con SMS no configurado (Twilio:FromNumber vacío) NO lanza.
    [Fact(DisplayName = "EC-N1 · notify requested → in-app al experto con magic-link, SMS off no peta")]
    public async Task NotifyRequested_creates_in_app_with_magic_link()
    {
        var (token, _, hireId, expertUserId, _) = await SeedPendingExpertConfirmationAsync();

        using (var scope = _api.Factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IAppointmentService>();
            await svc.NotifyExpertConfirmationRequestedAsync(hireId); // no debe lanzar (SMS gated)
        }

        await using var db = _api.CreateDbContext();
        var notif = await db.Notifications.AsNoTracking()
            .Where(n => n.UserId == expertUserId && n.Type == "expert_confirmation_requested")
            .OrderByDescending(n => n.CreatedAt)
            .FirstOrDefaultAsync();
        notif.Should().NotBeNull("debe crearse la notificación in-app al experto");
        notif!.Url.Should().Contain("/appointment/confirm/").And.Contain(token,
            "el enlace in-app es el magic-link con el token");
    }

    // ── EC-N2: approve → notificación in-app al CLIENTE (cita confirmada).
    [Fact(DisplayName = "EC-N2 · approve → in-app al cliente (cita confirmada)")]
    public async Task Approve_notifies_client_confirmed()
    {
        var (token, _, hireId, _, _) = await SeedPendingExpertConfirmationAsync();
        int clientId;
        await using (var db0 = _api.CreateDbContext())
            clientId = (await db0.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId)).ClientId!.Value;

        var res = await _api.Client.PostAsync($"/api/expert-confirmation/{token}/approve", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        await using var db = _api.CreateDbContext();
        var notif = await db.Notifications.AsNoTracking()
            .AnyAsync(n => n.UserId == clientId && n.Type == "expert_confirmation_approved");
        notif.Should().BeTrue("approve notifica al cliente que la cita está confirmada");
    }

    // ── EC-N3: reject → in-app al CLIENTE (reembolso) + in-app al EXPERTO (rechazada).
    [Fact(DisplayName = "EC-N3 · reject → in-app cliente (reembolso) + experto (rechazada)")]
    public async Task Reject_notifies_client_refund_and_expert_rejected()
    {
        var (token, _, hireId, expertUserId, _) = await SeedPendingExpertConfirmationAsync();
        int clientId;
        await using (var db0 = _api.CreateDbContext())
            clientId = (await db0.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId)).ClientId!.Value;

        var res = await _api.Client.PostAsync($"/api/expert-confirmation/{token}/reject", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        await using var db = _api.CreateDbContext();
        (await db.Notifications.AsNoTracking()
            .AnyAsync(n => n.UserId == clientId && n.Type == "expert_confirmation_refunded"))
            .Should().BeTrue("reject notifica al cliente el reembolso 100%");
        (await db.Notifications.AsNoTracking()
            .AnyAsync(n => n.UserId == expertUserId && n.Type == "expert_confirmation_rejected"))
            .Should().BeTrue("reject notifica al experto que rechazó la cita");
    }

    // ── EC-N4: timeout (timer expira pending) → in-app al cliente (reembolso) + experto (caducada).
    [Fact(DisplayName = "EC-N4 · timeout → in-app cliente (reembolso) + experto (caducada)")]
    public async Task Timeout_notifies_client_refund_and_expert_timeout()
    {
        var (_, _, hireId, expertUserId, _) = await SeedPendingExpertConfirmationAsync();
        int clientId;
        await using (var db0 = _api.CreateDbContext())
            clientId = (await db0.SearchHires.AsNoTracking().SingleAsync(h => h.Id == hireId)).ClientId!.Value;
        var timerId = await SeedExpertConfirmationTimerAsync(hireId, DateTime.UtcNow.AddSeconds(-1));

        using (var scope = _api.Factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IAppointmentService>();
            await svc.ProcessAppointmentTimerAsync(timerId);
        }

        await using var db = _api.CreateDbContext();
        (await db.Notifications.AsNoTracking()
            .AnyAsync(n => n.UserId == clientId && n.Type == "expert_confirmation_refunded"))
            .Should().BeTrue("el timeout notifica al cliente el reembolso 100%");
        (await db.Notifications.AsNoTracking()
            .AnyAsync(n => n.UserId == expertUserId && n.Type == "expert_confirmation_timeout"))
            .Should().BeTrue("el timeout notifica al experto la caducidad");
    }

    // ── EC-N5: recordatorios — ventana 48h → 2 reminders + 1 admin alert programados (3 jobIds en Notes).
    [Fact(DisplayName = "EC-N5 · reminders ventana 48h → 3 jobs en Notes (50%/80%/90%)")]
    public async Task ScheduleReminders_48h_window_stores_three_jobs()
    {
        var (_, _, hireId, _, _) = await SeedPendingExpertConfirmationAsync();
        var start = DateTime.UtcNow;
        var end = start.AddHours(48);
        var timerId = await SeedExpertConfirmationTimerAsync(hireId, end);
        int apptId;
        await using (var db0 = _api.CreateDbContext())
            apptId = await db0.Appointments.Where(a => a.SearchHireId == hireId).Select(a => a.Id).SingleAsync();

        using (var scope = _api.Factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IAppointmentService>();
            await svc.ScheduleExpertConfirmationRemindersAsync(timerId, hireId, apptId, start, end);
        }

        await using var db = _api.CreateDbContext();
        var notes = await db.AppointmentTimers.AsNoTracking().Where(t => t.Id == timerId).Select(t => t.Notes).SingleAsync();
        notes.Should().NotBeNullOrWhiteSpace("debe guardar los jobIds de los recordatorios");
        notes!.Split(',', StringSplitOptions.RemoveEmptyEntries).Length.Should().Be(3,
            "ventana 48h programa 2 reminders (50%/80%) + 1 admin alert (90%)");
    }

    // ── EC-N6: recordatorios — ventana <15min → NO programa nada (Notes vacío).
    [Fact(DisplayName = "EC-N6 · reminders ventana <15min → ninguno (Notes vacío)")]
    public async Task ScheduleReminders_short_window_schedules_none()
    {
        var (_, _, hireId, _, _) = await SeedPendingExpertConfirmationAsync();
        var start = DateTime.UtcNow;
        var end = start.AddMinutes(10); // <15min
        var timerId = await SeedExpertConfirmationTimerAsync(hireId, end);
        int apptId;
        await using (var db0 = _api.CreateDbContext())
            apptId = await db0.Appointments.Where(a => a.SearchHireId == hireId).Select(a => a.Id).SingleAsync();

        using (var scope = _api.Factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IAppointmentService>();
            await svc.ScheduleExpertConfirmationRemindersAsync(timerId, hireId, apptId, start, end);
        }

        await using var db = _api.CreateDbContext();
        var notes = await db.AppointmentTimers.AsNoTracking().Where(t => t.Id == timerId).Select(t => t.Notes).SingleAsync();
        notes.Should().BeNullOrEmpty("ventana <15min no programa recordatorios escalonados");
    }
}
