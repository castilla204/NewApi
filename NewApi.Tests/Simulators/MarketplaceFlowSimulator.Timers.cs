using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace NewApi.Tests.Simulators;

/// <summary>
/// Extensión TIMER de <see cref="MarketplaceFlowSimulator"/>.
///
/// Reproduce el contrato observable de los 5 timers de AppointmentService:
///
///   - proposal              · 24h desde awaiting_appointment → no propuesta del cliente
///                             → appointment_cancelled_by_client_no_proposal (0/100/0)
///   - response              · 24h desde appointment_proposed → no respuesta del experto
///                             → appointment_cancelled_by_expert_no_response (100/0/0)
///   - awaiting_report_transition · 3h post-cita confirmada → NO finaliza,
///                             solo transiciona appointment → appointment_awaiting_report
///                             y crea el timer expert_report 24h
///   - expert_report         · 24h desde awaiting_report → no reporte del experto
///                             → appointment_cancelled_by_no_report (95/0/5).
///                             SKIP money si hay Dispute Pending/Resolving (R27-T27-1-2,
///                             AppointmentService.cs:3857-3872).
///   - client_decision       · 24h desde report_sent → cliente no decide
///                             → appointment_completed_without_client_approval (0/95/5).
///                             SKIP money si hay Dispute Pending/Resolving (A-ii guard,
///                             AppointmentService.cs:3974-3988).
///
/// Watchdog (cron */10 * * * * UTC, Program.cs:1843-1847):
///   ProcessOverdueTimersAsync escanea timers IsExpired=false AND EndTime &lt;= UtcNow,
///   ORDER BY EndTime ASC, LIMIT 500 (AppointmentService.cs:3227-3232) y los despacha
///   al handler correcto por TimerType. Idempotente: ProcessAppointmentTimerAsync hace
///   un UPDATE atómico (3396-3401) que solo deja a UN ejecutor avanzar; los demás se
///   van. Si IsExpired=true ya, sale en 3382-3385. Limpia HangfireJobId NO lo hace
///   por defecto el handler, pero el wrapper (cuando un usuario cancela) sí (líneas
///   4324, 488 del simulator existente). En el simulator, limpiamos HangfireJobId al
///   expirar para reflejar el comportamiento esperado del wrapper completo.
///
/// El catálogo del usuario pidió 200 timers/run; el código real usa 500 (línea 3230,
/// "R5-C2 FIX: limit batch a 500 timers por ejecución"). Para no romper el catálogo
/// pero ser fiel al código, exponemos const <see cref="WatchdogBatchSize"/> = 500.
/// </summary>
public static partial class MarketplaceFlowSimulator
{
    /// <summary>
    /// Batch máximo del watchdog (fidelidad al código real: AppointmentService.cs:3230).
    /// </summary>
    public const int WatchdogBatchSize = 500;

    // ─────────────────────────────────────────────────────────────────────────
    // SCHEDULE — crea un timer activo (IsExpired=false) con HangfireJobId fake
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Programa un timer para un appointment. Equivalente al SaveChanges +
    /// BackgroundJob.Schedule del código real (AppointmentService.cs:4519-4553).
    /// </summary>
    public static async Task<AppointmentTimer> ScheduleAppointmentTimerAsync(
        AppDbContext db,
        int appointmentId,
        string timerType,
        TimeSpan durationFromNow,
        string? notes = null)
    {
        var now = DateTime.UtcNow;
        var timer = new AppointmentTimer
        {
            AppointmentId = appointmentId,
            TimerType = timerType,
            StartTime = now,
            EndTime = now.Add(durationFromNow),
            IsExpired = false,
            CreatedAt = now,
            Notes = notes,
            // jobId sintético — el simulator no schedula realmente en Hangfire
            HangfireJobId = "job_sim_" + Guid.NewGuid().ToString("N"),
        };
        db.AppointmentTimers.Add(timer);
        await db.SaveChangesAsync();
        return timer;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FAST-FORWARD helper para forzar vencimiento sin esperar 24h reales
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mueve EndTime de un timer activo al pasado (1 minuto atrás) para forzar
    /// que el watchdog lo recoja. Solo afecta timers IsExpired=false.
    /// </summary>
    public static async Task FastForwardTimerAsync(
        AppDbContext db, int timerId)
    {
        var timer = await db.AppointmentTimers.SingleAsync(t => t.Id == timerId);
        if (timer.IsExpired)
            throw new InvalidOperationException(
                $"Timer {timerId} ya está expirado — no se puede fast-forward");
        timer.EndTime = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EXPIRE handlers por tipo
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Timer 'proposal' vence: el cliente no propuso fecha en 24h.
    /// Appointment → appointment_cancelled_by_client_no_proposal (0/100/0).
    /// SearchHire → cancelled (via StatusMapping).
    /// AppointmentService.cs · case "proposal" 3534-3685.
    /// </summary>
    public static async Task ExpireProposalTimerAsync(AppDbContext db, int hireId)
    {
        var hire = await LoadHireAsync(db, hireId);

        // Validación de estado: el código real (líneas 3456-3489) solo procesa si
        // SearchHire en "pending" y Appointment en awaiting_appointment / cancelled_*.
        var hireStatusValue = hire.Status.StatusValue;
        if (hireStatusValue != "pending")
        {
            await MarkExpiredAndExitAsync(db, hire.Id, "proposal");
            return; // SearchHire no en pending → consumir timer sin actuar
        }

        await ProcessMoneyDistributionAsync(db, hire,
            "appointment_cancelled_by_client_no_proposal");

        // Cambiar SearchHire → cancelled (mapping de AppointmentStatus a SearchHireStatus)
        // ProcessMoneyDistributionAsync ya lo hace en su Fase 2 cuando updateState=true,
        // pero el simulator de splits no toca el estado del hire. Lo aplicamos aquí.
        var cancelledHireStatus = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" && s.StatusValue == "cancelled");
        if (hire.StatusId != cancelledHireStatus.Id)
        {
            hire.StatusId = cancelledHireStatus.Id;
            hire.UpdatedAt = DateTime.UtcNow;
        }

        // Cambiar Appointment al estado específico
        var appt = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id);
        var noProposalStatus = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" &&
            s.StatusValue == "appointment_cancelled_by_client_no_proposal");
        appt.StatusId = noProposalStatus.Id;
        appt.UpdatedAt = DateTime.UtcNow;

        // Marcar timer como expirado (claim atómico simulado)
        await ExpireTimerByTypeAsync(db, appt.Id, "proposal");

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Timer 'response' vence: el experto no respondió a la propuesta del cliente en 24h.
    /// Appointment → appointment_cancelled_by_expert_no_response (100/0/0).
    /// SearchHire → cancelled.
    /// AppointmentService.cs · case "response" 3687-3836.
    /// </summary>
    public static async Task ExpireResponseTimerAsync(AppDbContext db, int hireId)
    {
        var hire = await LoadHireAsync(db, hireId);
        if (hire.Status.StatusValue != "pending")
        {
            await MarkExpiredAndExitAsync(db, hire.Id, "response");
            return;
        }

        await ProcessMoneyDistributionAsync(db, hire,
            "appointment_cancelled_by_expert_no_response");

        var cancelledHireStatus = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" && s.StatusValue == "cancelled");
        if (hire.StatusId != cancelledHireStatus.Id)
        {
            hire.StatusId = cancelledHireStatus.Id;
            hire.UpdatedAt = DateTime.UtcNow;
        }

        var appt = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id);
        var noResponseStatus = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" &&
            s.StatusValue == "appointment_cancelled_by_expert_no_response");
        appt.StatusId = noResponseStatus.Id;
        appt.UpdatedAt = DateTime.UtcNow;

        await ExpireTimerByTypeAsync(db, appt.Id, "response");

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Timer 'awaiting_report_transition' vence (3h post-cita): NO finaliza.
    /// Transiciona Appointment → appointment_awaiting_report y crea timer expert_report 24h.
    /// AppointmentService.cs · ProcessAppointmentToAwaitingReportAsync 4444-4553.
    /// Re-entrante: si ya existe un expert_report activo, sale sin crear duplicado (FIX #7).
    /// </summary>
    public static async Task<AppointmentTimer?> FireAwaitingReportTransitionAsync(
        AppDbContext db, int hireId)
    {
        var hire = await LoadHireAsync(db, hireId);
        var appt = await db.Appointments
            .Include(a => a.Status)
            .SingleAsync(a => a.SearchHireId == hire.Id);

        // Guard: solo procesar si la cita está confirmed (real: línea 4461)
        if (appt.Status.StatusValue != "appointment_confirmed")
        {
            await ExpireTimerByTypeAsync(db, appt.Id, "awaiting_report_transition");
            await db.SaveChangesAsync();
            return null;
        }

        // FIX #7 / G5: si ya hay expert_report activo, no duplicar
        var alreadyActive = await db.AppointmentTimers.AnyAsync(t =>
            t.AppointmentId == appt.Id && t.TimerType == "expert_report" && !t.IsExpired);
        if (alreadyActive)
        {
            await ExpireTimerByTypeAsync(db, appt.Id, "awaiting_report_transition");
            await db.SaveChangesAsync();
            return null;
        }

        var awaitingReport = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_awaiting_report");
        appt.StatusId = awaitingReport.Id;
        appt.UpdatedAt = DateTime.UtcNow;

        // Crear timer expert_report 24h
        var expertReportTimer = new AppointmentTimer
        {
            AppointmentId = appt.Id,
            TimerType = "expert_report",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(24),
            IsExpired = false,
            CreatedAt = DateTime.UtcNow,
            HangfireJobId = "job_sim_" + Guid.NewGuid().ToString("N"),
        };
        db.AppointmentTimers.Add(expertReportTimer);

        // Marcar el transition timer como expirado (línea 4582-4593 del código real)
        await ExpireTimerByTypeAsync(db, appt.Id, "awaiting_report_transition");

        await db.SaveChangesAsync();
        return expertReportTimer;
    }

    /// <summary>
    /// Timer 'expert_report' vence (24h): experto no entregó reporte.
    /// Appointment → appointment_cancelled_by_no_report (95/0/5).
    /// SearchHire → cancelled.
    /// AppointmentService.cs · case "expert_report" 3838-3963.
    /// GUARD R27-T27-1-2 (línea 3857): si hay Dispute Pending/Resolving, SKIP money
    /// distribution — la disputa lo gestiona. Solo se marca el timer expirado.
    /// </summary>
    public static async Task ExpireExpertReportTimerWithDisputeGuardAsync(
        AppDbContext db, int hireId)
    {
        var hire = await LoadHireAsync(db, hireId);
        var appt = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id);

        // Guard R27-T27-1-2: si hay disputa pendiente, NO procesar dinero.
        var hasPendingDispute = await db.Disputes.AnyAsync(d =>
            d.SearchHireId == hire.Id && (d.Status == "Pending" || d.Status == "Resolving"));
        if (hasPendingDispute)
        {
            // Solo marca el timer expirado — el SaveChanges final lo persiste (línea 3871).
            await ExpireTimerByTypeAsync(db, appt.Id, "expert_report");
            await db.SaveChangesAsync();
            return;
        }

        await ProcessMoneyDistributionAsync(db, hire, "appointment_cancelled_by_no_report");

        var cancelledHireStatus = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" && s.StatusValue == "cancelled");
        if (hire.StatusId != cancelledHireStatus.Id)
        {
            hire.StatusId = cancelledHireStatus.Id;
            hire.UpdatedAt = DateTime.UtcNow;
        }

        var noReport = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" &&
            s.StatusValue == "appointment_cancelled_by_no_report");
        appt.StatusId = noReport.Id;
        appt.UpdatedAt = DateTime.UtcNow;

        await ExpireTimerByTypeAsync(db, appt.Id, "expert_report");
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Timer 'client_decision' vence (24h): cliente no aprobó ni disputó.
    /// Appointment → appointment_completed_without_client_approval (0/95/5).
    /// SearchHire → completed.
    /// AppointmentService.cs · case "client_decision" 3965-4128.
    /// GUARD A-ii (línea 3974): si hay Dispute Pending/Resolving, SKIP auto-payout.
    /// </summary>
    public static async Task ExpireClientDecisionTimerWithDisputeGuardAsync(
        AppDbContext db, int hireId)
    {
        var hire = await LoadHireAsync(db, hireId);
        var appt = await db.Appointments.SingleAsync(a => a.SearchHireId == hire.Id);

        var hasPendingDispute = await db.Disputes.AnyAsync(d =>
            d.SearchHireId == hire.Id && (d.Status == "Pending" || d.Status == "Resolving"));
        if (hasPendingDispute)
        {
            await ExpireTimerByTypeAsync(db, appt.Id, "client_decision");
            await db.SaveChangesAsync();
            return;
        }

        await ProcessMoneyDistributionAsync(db, hire,
            "appointment_completed_without_client_approval");

        // SearchHire → completed (mapping)
        var completedHire = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" && s.StatusValue == "completed");
        if (hire.StatusId != completedHire.Id)
        {
            hire.StatusId = completedHire.Id;
            hire.UpdatedAt = DateTime.UtcNow;
        }

        var autoCompleted = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" &&
            s.StatusValue == "appointment_completed_without_client_approval");
        appt.StatusId = autoCompleted.Id;
        appt.UpdatedAt = DateTime.UtcNow;

        await ExpireTimerByTypeAsync(db, appt.Id, "client_decision");
        await db.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WATCHDOG · ProcessOverdueTimersAsync
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Replica el bucle de ProcessOverdueTimersAsync (AppointmentService.cs:3219-3340):
    ///   - SELECT timers WHERE !IsExpired AND EndTime &lt;= UtcNow ORDER BY EndTime LIMIT 500
    ///   - Despacha por TimerType al handler correcto
    ///   - Continúa aunque un timer individual falle (try/catch por iteración)
    ///   - Idempotente: si un timer ya tiene IsExpired=true cuando lo procesa el
    ///     handler, sale silenciosamente
    /// </summary>
    /// <returns>Número de timers procesados (que pasaron el guard !IsExpired al inicio).</returns>
    public static async Task<int> RunWatchdogAsync(AppDbContext db)
    {
        var nowUtc = DateTime.UtcNow;

        // Snapshot inicial (línea 3227-3232 real). Ordenado por EndTime ASC para procesar
        // los más antiguos primero.
        var overdue = await db.AppointmentTimers
            .Where(t => !t.IsExpired && t.EndTime <= nowUtc)
            .OrderBy(t => t.EndTime)
            .Take(WatchdogBatchSize)
            .Select(t => new { t.Id, t.TimerType, t.AppointmentId })
            .ToListAsync();

        int processed = 0;
        foreach (var snap in overdue)
        {
            try
            {
                // El handler real es idempotente — re-leemos el timer fresh por si otro
                // ejecutor ya lo claim-eó (replicas:2 en prod). Aquí los tests son single-
                // process pero respetamos el contrato.
                var fresh = await db.AppointmentTimers
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == snap.Id);
                if (fresh == null || fresh.IsExpired)
                {
                    continue;
                }

                var hireId = await db.Appointments
                    .Where(a => a.Id == snap.AppointmentId)
                    .Select(a => a.SearchHireId)
                    .FirstOrDefaultAsync();
                if (hireId == 0) continue;

                switch (snap.TimerType)
                {
                    case "proposal":
                        await ExpireProposalTimerAsync(db, hireId);
                        break;
                    case "response":
                        await ExpireResponseTimerAsync(db, hireId);
                        break;
                    case "awaiting_report_transition":
                        await FireAwaitingReportTransitionAsync(db, hireId);
                        break;
                    case "expert_report":
                        await ExpireExpertReportTimerWithDisputeGuardAsync(db, hireId);
                        break;
                    case "client_decision":
                        await ExpireClientDecisionTimerWithDisputeGuardAsync(db, hireId);
                        break;
                    default:
                        // Tipos desconocidos: el handler real los consume (sale por estado
                        // inválido en el switch). Aquí simplemente marcamos expirado.
                        await ExpireTimerByIdAsync(db, snap.Id);
                        await db.SaveChangesAsync();
                        break;
                }
                processed++;
            }
            catch
            {
                // Watchdog real (línea 3251-3263): captura y continúa con el siguiente.
                // Limpiamos ChangeTracker para no contaminar la siguiente iteración (N22 FIX).
                db.ChangeTracker.Clear();
            }
            finally
            {
                // N22 FIX (línea 3266-3272): limpiar SIEMPRE el tracker.
                db.ChangeTracker.Clear();
            }
        }

        return processed;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HELPERS internos
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Marca todos los timers ACTIVOS de tipo dado para un appointment como expirados
    /// y limpia HangfireJobId (el wrapper real lo limpia tras consumir el job).
    /// </summary>
    private static async Task ExpireTimerByTypeAsync(
        AppDbContext db, int appointmentId, string timerType)
    {
        var active = await db.AppointmentTimers
            .Where(t => t.AppointmentId == appointmentId &&
                        t.TimerType == timerType &&
                        !t.IsExpired)
            .ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var t in active)
        {
            t.IsExpired = true;
            t.ExpiredAt = now;
            t.HangfireJobId = null;
        }
    }

    private static async Task ExpireTimerByIdAsync(AppDbContext db, int timerId)
    {
        var t = await db.AppointmentTimers.SingleAsync(x => x.Id == timerId);
        if (t.IsExpired) return;
        t.IsExpired = true;
        t.ExpiredAt = DateTime.UtcNow;
        t.HangfireJobId = null;
    }

    /// <summary>
    /// Salida temprana cuando el SearchHire no está en el estado esperado para el
    /// tipo de timer — replica los early-return de las líneas 3458-3525 del código
    /// real (marca timer expirado SIN actuar sobre dinero o estado del hire).
    /// </summary>
    private static async Task MarkExpiredAndExitAsync(
        AppDbContext db, int hireId, string timerType)
    {
        var apptId = await db.Appointments
            .Where(a => a.SearchHireId == hireId)
            .Select(a => a.Id)
            .FirstOrDefaultAsync();
        if (apptId == 0) return;
        await ExpireTimerByTypeAsync(db, apptId, timerType);
        await db.SaveChangesAsync();
    }
}
