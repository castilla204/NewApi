using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace NewApi.Tests.Simulators;

/// <summary>
/// EXTENSIÓN — Lifecycle completo del Appointment (propose → confirm → +3h → report → approve).
///
/// Estos métodos modelan fielmente las transiciones de AppointmentService.cs verificadas
/// contra el código real:
///
///   • ProposeAppointmentAsync (AppointmentService.cs:648-1161)
///       - Source: awaiting_appointment | appointment_rejected
///                 | appointment_cancelled_by_client | appointment_cancelled_by_expert
///       - Target: appointment_proposed
///       - Expira timers 'proposal' activos + crea timer 'response' (24h).
///
///   • ConfirmAppointmentAsync (AppointmentService.cs:1165-1780)
///       - Source: appointment_proposed (estricto)
///       - Target: appointment_confirmed
///       - Expira timers 'response' + crea timer 'awaiting_report_transition' (cita+3h).
///       - NO crea FT.
///
///   • RejectAppointmentAsync (AppointmentService.cs:1784-2432)
///       - Source: appointment_proposed
///       - 1er rechazo → appointment_rejected, RejectionCount++, crea timer 'proposal' (24h), NO money.
///       - 2do rechazo (RejectionCount >= 1) → appointment_cancelled_by_expert_rejection,
///         ExpertCancellationCount++, ProcessMoneyDistribution (split de StatusConfiguration).
///
///   • ProcessAppointmentToAwaitingReportAsync (AppointmentService.cs:4444-4604)
///       - Source: appointment_confirmed + SearchHire pending|awaiting_client_decision
///       - Target: appointment_awaiting_report + timer 'expert_report' (24h).
///       - Expira timer 'awaiting_report_transition'.
///
///   • SubmitExpertReportAsync (AppointmentService.cs:4608-5042)
///       - Source: appointment_awaiting_report (estricto)
///       - Target: appointment_report_sent + SearchHire → awaiting_client_decision.
///       - Expira TODOS los timers activos + crea timer 'client_decision' (24h).
///
///   • SearchHireController.CompleteService (SearchHireController.cs:1072-1412)
///       - Source SearchHire: pending | awaiting_client_decision (validación L1135).
///       - Cliente=ClientId, ClientApproved obligatorio (true; false fuerza usar /Dispute).
///       - ProcessMoneyDistribution("completed") + cierra timer 'client_decision' + appt completed.
/// </summary>
public static partial class MarketplaceFlowSimulator
{
    // ─────────────────────────────────────────────────────────────────────────
    // STEP · Cliente propone fecha
    // ─────────────────────────────────────────────────────────────────────────
    public static async Task<Appointment> ClientProposesAppointmentAsync(
        AppDbContext db, int hireId, int clientUserId, DateTime proposedDate)
    {
        var hire = await db.SearchHires
            .Include(h => h.Status)
            .SingleAsync(h => h.Id == hireId);

        if (hire.ClientId != clientUserId)
            throw new UnauthorizedAccessException(
                $"Usuario {clientUserId} no es el ClientId del hire {hireId}");

        // El simulador asume que el Appointment ya existe (creado por AttachAppointmentAsync
        // o por una propuesta previa). El código real lo crea inline si falta — replicamos eso.
        var appt = await db.Appointments
            .Include(a => a.Status)
            .SingleOrDefaultAsync(a => a.SearchHireId == hireId);

        if (appt is null)
        {
            var awaitingStatus = await db.SystemStatuses.SingleAsync(s =>
                s.StatusType == "AppointmentStatus" && s.StatusValue == "awaiting_appointment");
            appt = new Appointment
            {
                SearchHireId = hireId,
                StatusId = awaitingStatus.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Appointments.Add(appt);
            await db.SaveChangesAsync();
            await db.Entry(appt).Reference(a => a.Status).LoadAsync();
        }

        // Validación de estados válidos para proponer
        // (AppointmentService.cs:831-836 + invalid 839-844)
        var validStates = new[]
        {
            "awaiting_appointment",
            "appointment_rejected",
            "appointment_cancelled_by_client",
            "appointment_cancelled_by_expert",
        };
        var currentStatus = appt.Status?.StatusValue ?? string.Empty;
        if (!validStates.Contains(currentStatus))
            throw new InvalidOperationException(
                $"No se puede proponer en estado '{currentStatus}'. " +
                $"Estados válidos: {string.Join(", ", validStates)}");

        // Expirar timers 'proposal' activos (L960-979)
        await ExpireOpenTimersAsync(db, appt, "proposal");

        // 🔧 Faithful: el código real NO expira 'response' aquí, pero CUALQUIER ruta que
        // dejara el appt en un estado válido para re-propose (cancel/reject) ya expira los
        // 'response' previos en su propia tx (ver CancelAppointmentAsync L2845-2867 y
        // RejectAppointmentAsync L2028-2064). Por eso en el código real nunca hay un
        // 'response' activo cuando llega un re-propose. Como el simulador de cancel-by-client
        // existente NO los expira, lo hacemos aquí defensivamente para evitar violar el
        // unique index IX_AppointmentTimers_Appt_Type_Active. Esto no cambia el resultado
        // observable porque siempre se respeta la invariante "max 1 timer activo por tipo".
        await ExpireOpenTimersAsync(db, appt, "response");

        // Transición a appointment_proposed
        var proposed = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_proposed");
        appt.StatusId = proposed.Id;
        appt.ProposedDate = DateTime.SpecifyKind(proposedDate.Date, DateTimeKind.Utc);
        appt.ProposedTime = proposedDate.TimeOfDay;
        appt.LastProposalAt = DateTime.UtcNow;
        appt.UpdatedAt = DateTime.UtcNow;

        // Crear timer 'response' 24h (L982-990)
        db.AppointmentTimers.Add(new AppointmentTimer
        {
            AppointmentId = appt.Id,
            TimerType = "response",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(24),
            IsExpired = false,
            CreatedAt = DateTime.UtcNow,
            HangfireJobId = "job_sim_resp_" + Guid.NewGuid().ToString("N"),
        });

        await db.SaveChangesAsync();
        return appt;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STEP · Experto confirma propuesta
    // ─────────────────────────────────────────────────────────────────────────
    public static async Task<Appointment> ExpertConfirmsAppointmentAsync(
        AppDbContext db, int hireId, int expertUserId)
    {
        var hire = await db.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == hireId);
        if (hire.ExpertId != expertUserId)
            throw new UnauthorizedAccessException(
                $"Usuario {expertUserId} no es el ExpertId del hire {hireId}");

        var appt = await db.Appointments
            .Include(a => a.Status)
            .SingleAsync(a => a.SearchHireId == hireId);

        var currentStatus = appt.Status?.StatusValue ?? string.Empty;
        if (currentStatus != "appointment_proposed")
            throw new InvalidOperationException(
                $"No se puede confirmar en estado '{currentStatus}'. " +
                $"Solo se confirman citas en estado 'appointment_proposed'.");

        // Transición (L1279-1281)
        var confirmed = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_confirmed");
        appt.StatusId = confirmed.Id;
        appt.LastResponseAt = DateTime.UtcNow;
        appt.UpdatedAt = DateTime.UtcNow;

        // Expirar timers 'response' (L1284-1309)
        await ExpireOpenTimersAsync(db, appt, "response");

        // Crear timer 'awaiting_report_transition' a cita+3h (L1324-1336).
        // En el código real la EndTime se calcula desde ProposedDate+Time+3h en UTC.
        // Aquí asumimos que la propuesta ya fue marcada y usamos +3h sobre now si no hay
        // fecha futura (el job revalida estado, idempotente).
        DateTime endTime;
        if (appt.ProposedDate.HasValue && appt.ProposedTime.HasValue)
        {
            // El test propone fechas FUTURAS sintéticas. Usamos esa hora + 3h.
            var apptUtc = DateTime.SpecifyKind(
                appt.ProposedDate.Value.Date + appt.ProposedTime.Value,
                DateTimeKind.Utc);
            endTime = apptUtc.AddHours(3);
        }
        else
        {
            endTime = DateTime.UtcNow.AddHours(3);
        }

        db.AppointmentTimers.Add(new AppointmentTimer
        {
            AppointmentId = appt.Id,
            TimerType = "awaiting_report_transition",
            StartTime = DateTime.UtcNow,
            EndTime = endTime,
            IsExpired = false,
            CreatedAt = DateTime.UtcNow,
            HangfireJobId = "job_sim_artr_" + Guid.NewGuid().ToString("N"),
        });

        await db.SaveChangesAsync();
        return appt;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STEP · Experto rechaza propuesta
    // ─────────────────────────────────────────────────────────────────────────
    public static async Task<string> ExpertRejectsAppointmentAsync(
        AppDbContext db, int hireId, int expertUserId)
    {
        var hire = await db.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == hireId);
        if (hire.ExpertId != expertUserId)
            throw new UnauthorizedAccessException();

        var appt = await db.Appointments
            .Include(a => a.Status)
            .SingleAsync(a => a.SearchHireId == hireId);

        var currentStatus = appt.Status?.StatusValue ?? string.Empty;
        if (currentStatus != "appointment_proposed")
            throw new InvalidOperationException(
                $"No se puede rechazar en estado '{currentStatus}'");

        bool isSecondRejection = appt.RejectionCount >= 1;
        string targetStatusValue = isSecondRejection
            ? "appointment_cancelled_by_expert_rejection"
            : "appointment_rejected";

        var targetStatus = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == targetStatusValue);

        appt.StatusId = targetStatus.Id;
        appt.RejectionCount++;
        if (isSecondRejection)
        {
            appt.ExpertCancellationCount++;
        }
        appt.LastRejectionAt = DateTime.UtcNow;
        appt.LastResponseAt = DateTime.UtcNow;
        appt.UpdatedAt = DateTime.UtcNow;

        // Expirar timers 'response' (L2028-2064)
        await ExpireOpenTimersAsync(db, appt, "response");

        if (isSecondRejection)
        {
            // 2do rechazo → money distribution con la penalización del experto (L2095-2104)
            // NO recargamos hire — el code real llama _refundService con el searchHireId.
            await db.SaveChangesAsync();
            await ProcessMoneyDistributionAsync(db, hire, "appointment_cancelled_by_expert_rejection");
        }
        else
        {
            // 1er rechazo: crear timer 'proposal' 24h para que el cliente re-proponga (L2226-2249).
            db.AppointmentTimers.Add(new AppointmentTimer
            {
                AppointmentId = appt.Id,
                TimerType = "proposal",
                StartTime = DateTime.UtcNow,
                EndTime = DateTime.UtcNow.AddHours(24),
                IsExpired = false,
                CreatedAt = DateTime.UtcNow,
                HangfireJobId = "job_sim_prop_" + Guid.NewGuid().ToString("N"),
            });
            await db.SaveChangesAsync();
        }

        return targetStatusValue;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STEP · Simula el job awaiting_report_transition (timer +3h expira)
    // ─────────────────────────────────────────────────────────────────────────
    public static async Task AdvanceToAwaitingReportAsync(AppDbContext db, int hireId)
    {
        var hire = await db.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == hireId);
        var appt = await db.Appointments
            .Include(a => a.Status)
            .SingleAsync(a => a.SearchHireId == hireId);

        // Guard de estado (AppointmentService.cs:4461) — no-op si no aplica.
        if (appt.Status?.StatusValue != "appointment_confirmed")
            return;

        // Guard de SearchHire (L4482)
        var shStatus = hire.Status?.StatusValue ?? string.Empty;
        if (shStatus != "pending" && shStatus != "awaiting_client_decision")
            return;

        // Guard de idempotencia (L4502): si ya hay un timer 'expert_report' activo, no-op.
        var hasActiveExpertReportTimer = await db.AppointmentTimers
            .AnyAsync(t => t.AppointmentId == appt.Id &&
                           t.TimerType == "expert_report" &&
                           !t.IsExpired);
        if (hasActiveExpertReportTimer) return;

        // Transición (L4509-4516)
        var awaitingReport = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_awaiting_report");
        appt.StatusId = awaitingReport.Id;
        appt.UpdatedAt = DateTime.UtcNow;

        // Crear timer 'expert_report' 24h (L4519-4527)
        db.AppointmentTimers.Add(new AppointmentTimer
        {
            AppointmentId = appt.Id,
            TimerType = "expert_report",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(24),
            IsExpired = false,
            CreatedAt = DateTime.UtcNow,
            HangfireJobId = "job_sim_er_" + Guid.NewGuid().ToString("N"),
        });

        // Expirar timer 'awaiting_report_transition' (L4582-4596)
        await ExpireOpenTimersAsync(db, appt, "awaiting_report_transition");

        await db.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STEP · Experto envía el reporte (versión completa con validaciones de estado)
    // ─────────────────────────────────────────────────────────────────────────
    public static async Task ExpertSubmitsReportAsync(
        AppDbContext db, int hireId, int expertUserId)
    {
        var hire = await db.SearchHires.Include(h => h.Status).SingleAsync(h => h.Id == hireId);
        if (hire.ExpertId != expertUserId)
            throw new UnauthorizedAccessException();

        var appt = await db.Appointments
            .Include(a => a.Status)
            .SingleAsync(a => a.SearchHireId == hireId);

        // ✅ FAITHFUL: el código real exige appointment_awaiting_report (L4674), NO confirmed.
        var currentStatus = appt.Status?.StatusValue ?? string.Empty;
        if (currentStatus != "appointment_awaiting_report")
            throw new InvalidOperationException(
                $"No se puede enviar reporte en estado '{currentStatus}'. " +
                $"Solo cuando la cita está en 'appointment_awaiting_report'.");

        // Transición appointment → report_sent (L4778)
        var reportSent = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "AppointmentStatus" && s.StatusValue == "appointment_report_sent");
        appt.StatusId = reportSent.Id;
        appt.UpdatedAt = DateTime.UtcNow;

        // SearchHire → awaiting_client_decision (L4756-4802 via mapping)
        var awaiting = await db.SystemStatuses.SingleAsync(s =>
            s.StatusType == "SearchHireStatus" && s.StatusValue == "awaiting_client_decision");
        hire.StatusId = awaiting.Id;
        hire.UpdatedAt = DateTime.UtcNow;

        // Cancelar TODOS los timers activos (L4812-4844)
        var activeTimers = await db.AppointmentTimers
            .Where(t => t.AppointmentId == appt.Id && !t.IsExpired)
            .ToListAsync();
        foreach (var t in activeTimers)
        {
            t.IsExpired = true;
            t.ExpiredAt = DateTime.UtcNow;
            t.HangfireJobId = null;
        }

        // Crear timer 'client_decision' 24h (L4850-4858)
        db.AppointmentTimers.Add(new AppointmentTimer
        {
            AppointmentId = appt.Id,
            TimerType = "client_decision",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(24),
            IsExpired = false,
            CreatedAt = DateTime.UtcNow,
            HangfireJobId = "job_sim_cd_" + Guid.NewGuid().ToString("N"),
        });

        await db.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STEP · Cliente aprueba desde awaiting_client_decision (helper específico)
    // ─────────────────────────────────────────────────────────────────────────
    public static async Task ClientApprovesReportFromAwaitingDecisionAsync(
        AppDbContext db, int hireId, int clientUserId)
    {
        var hire = await db.SearchHires
            .Include(h => h.Status)
            .SingleAsync(h => h.Id == hireId);

        if (hire.ClientId != clientUserId)
            throw new UnauthorizedAccessException();

        if (hire.Status?.StatusValue != "awaiting_client_decision")
            throw new InvalidOperationException(
                $"Hire en estado '{hire.Status?.StatusValue}' — este helper exige awaiting_client_decision específicamente.");

        // Reusamos el path del ClientApprovesServiceAsync existente; el efecto es idéntico.
        await ClientApprovesServiceAsync(db, hireId, clientUserId);
    }
}
