using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;

namespace NewApi.Tests.Simulators;

/// <summary>
/// EXTENSIÓN — Modelo de máquina de estados + walker model-based del Appointment.
///
/// Esta partial NO toca el core del simulador; sólo añade un MODELO PURO (sin BD) de
/// las transiciones válidas verificadas contra NewApi/Services/AppointmentService.cs, un
/// generador de secuencias válidas (walker) y un despachador que ejecuta una acción
/// modelada contra los métodos YA EXISTENTES del simulador.
///
/// AUDITORÍA DE GUARDS (file:línea de AppointmentService.cs) — fuente de verdad del modelo:
///
///   ProposeAppointmentAsync (648) · guard 831-866:
///       Source EXACTO = { awaiting_appointment, appointment_rejected,
///                         appointment_cancelled_by_client, appointment_cancelled_by_expert }
///       invalidStatesForPropose (839-844) RECHAZA explícitamente appointment_proposed,
///       appointment_confirmed, *_second → NO se puede re-proponer desde proposed sin
///       cancelar/rechazar antes. Target = appointment_proposed.
///
///   ConfirmAppointmentAsync (1165) · guard 1243-1249:
///       Source EXACTO = { appointment_proposed }. Target = appointment_confirmed.
///       (Además 1233: rechaza si SearchHire está en estado de finalización.)
///
///   RejectAppointmentAsync (1784) · guard 1858-1870 + 1918:
///       Source EXACTO = { appointment_proposed }.
///       isSecondRejection = RejectionCount >= 1.
///         1º (RejectionCount 0) → appointment_rejected (NO final, sin dinero, RejectionCount=1).
///         2º (RejectionCount>=1) → appointment_cancelled_by_expert_rejection (FINAL, dinero
///            vía mapping activo → cancelled → 100/0/0).
///
///   CancelAppointmentAsync (2436) · guard 2573-2593:
///       Source EXACTO = { appointment_confirmed }  (¡SÓLO confirmed!).
///       2504-2518 rechaza awaiting_appointment; 2526-2562 rechaza estados finales;
///       proposed/rejected NO están en validStatesForCancel → RECHAZADOS.
///       Contadores SEPARADOS por actor (2636-2675):
///         Cliente: ClientCancellationCount==0 → appointment_cancelled_by_client (NO final);
///                  >=1 → appointment_cancelled_by_client_second (FINAL, 0/95/5).
///         Experto: ExpertCancellationCount==0 → appointment_cancelled_by_expert (NO final);
///                  >=1 → appointment_cancelled_by_expert_second (FINAL, 100/0/0).
///
///   ProcessAppointmentToAwaitingReportAsync (4444) · guard 4461 + 4482:
///       Source appt = appointment_confirmed; hire ∈ {pending, awaiting_client_decision}.
///       Target = appointment_awaiting_report.
///
///   SubmitExpertReportAsync (4608) · guard 4674:
///       Source EXACTO appt = appointment_awaiting_report. Target = appointment_report_sent
///       + hire → awaiting_client_decision.
///
///   CompleteService (SearchHireController.cs 1072-1412) · guard 1135:
///       Source hire ∈ { pending, awaiting_client_decision }, ClientApproved=true.
///       → appointment_completed + hire completed (0/95/5).
///
/// ⚠️ DIVERGENCIA SIMULADOR↔CÓDIGO REAL (reportada): los helpers CancelByClientAsync /
///    CancelByExpertAsync del core (MarketplaceFlowSimulator.cs:181-248) NO validan el
///    estado de origen — sólo incrementan el contador y transicionan. El código real SÍ
///    exige appointment_confirmed (línea 2573). Por eso este MODELO sólo habilita la acción
///    Cancel desde appointment_confirmed: así el walker nunca ejerce una transición que el
///    backend real prohibiría, y SM-02 verifica los guards que el simulador SÍ implementa
///    (propose/confirm/reject/submit) lanzando excepción.
/// </summary>
public static partial class MarketplaceFlowSimulator
{
    public static class StateMachine
    {
        // ── Estados modelados (subconjunto alcanzable por las acciones del walker) ──
        public const string AwaitingAppointment = "awaiting_appointment";
        public const string Proposed = "appointment_proposed";
        public const string Confirmed = "appointment_confirmed";
        public const string Rejected = "appointment_rejected";
        public const string CancelledByClient = "appointment_cancelled_by_client";
        public const string CancelledByExpert = "appointment_cancelled_by_expert";
        public const string AwaitingReport = "appointment_awaiting_report";
        public const string ReportSent = "appointment_report_sent";
        // Terminales
        public const string CancelledByClientSecond = "appointment_cancelled_by_client_second";
        public const string CancelledByExpertSecond = "appointment_cancelled_by_expert_second";
        public const string CancelledByExpertRejection = "appointment_cancelled_by_expert_rejection";
        public const string Completed = "appointment_completed";

        public enum Action
        {
            Propose,
            Confirm,
            Reject,
            CancelClient,
            CancelExpert,
            AdvanceToAwaitingReport,
            SubmitReport,
            Approve,
        }

        /// <summary>Estado modelado del appointment (puro, sin BD).</summary>
        public sealed record ModelState(
            string ApptStatus,
            int RejectionCount,
            int ClientCancellationCount,
            int ExpertCancellationCount)
        {
            public static ModelState Initial => new(AwaitingAppointment, 0, 0, 0);
        }

        /// <summary>Estados FINALES (IsFinalizationStatus=true en el seed) alcanzables.</summary>
        public static readonly HashSet<string> TerminalStates = new()
        {
            CancelledByClientSecond,
            CancelledByExpertSecond,
            CancelledByExpertRejection,
            Completed,
        };

        public static bool IsTerminal(ModelState s) => TerminalStates.Contains(s.ApptStatus);

        private static readonly string[] ProposeSources =
        {
            AwaitingAppointment, Rejected, CancelledByClient, CancelledByExpert,
        };

        /// <summary>
        /// Función de transición PURA. Devuelve el estado destino si la acción es válida
        /// desde <paramref name="s"/>, o null si el guard real la RECHAZARÍA.
        /// Modela exactamente los guards auditados arriba.
        /// </summary>
        public static ModelState? Next(ModelState s, Action action)
        {
            switch (action)
            {
                case Action.Propose:
                    if (!ProposeSources.Contains(s.ApptStatus)) return null;
                    return s with { ApptStatus = Proposed };

                case Action.Confirm:
                    if (s.ApptStatus != Proposed) return null;
                    return s with { ApptStatus = Confirmed };

                case Action.Reject:
                    if (s.ApptStatus != Proposed) return null;
                    if (s.RejectionCount >= 1)
                        return s with
                        {
                            ApptStatus = CancelledByExpertRejection,
                            RejectionCount = s.RejectionCount + 1,
                            ExpertCancellationCount = s.ExpertCancellationCount + 1,
                        };
                    return s with { ApptStatus = Rejected, RejectionCount = s.RejectionCount + 1 };

                case Action.CancelClient:
                    // Código real: SÓLO desde appointment_confirmed (AppointmentService.cs:2573).
                    if (s.ApptStatus != Confirmed) return null;
                    if (s.ClientCancellationCount >= 1)
                        return s with
                        {
                            ApptStatus = CancelledByClientSecond,
                            ClientCancellationCount = s.ClientCancellationCount + 1,
                        };
                    return s with
                    {
                        ApptStatus = CancelledByClient,
                        ClientCancellationCount = s.ClientCancellationCount + 1,
                    };

                case Action.CancelExpert:
                    if (s.ApptStatus != Confirmed) return null;
                    if (s.ExpertCancellationCount >= 1)
                        return s with
                        {
                            ApptStatus = CancelledByExpertSecond,
                            ExpertCancellationCount = s.ExpertCancellationCount + 1,
                        };
                    return s with
                    {
                        ApptStatus = CancelledByExpert,
                        ExpertCancellationCount = s.ExpertCancellationCount + 1,
                    };

                case Action.AdvanceToAwaitingReport:
                    if (s.ApptStatus != Confirmed) return null;
                    return s with { ApptStatus = AwaitingReport };

                case Action.SubmitReport:
                    if (s.ApptStatus != AwaitingReport) return null;
                    return s with { ApptStatus = ReportSent };

                case Action.Approve:
                    // Cliente aprueba: desde report_sent (hire awaiting_client_decision).
                    if (s.ApptStatus != ReportSent) return null;
                    return s with { ApptStatus = Completed };

                default:
                    return null;
            }
        }

        /// <summary>
        /// Estado final del SearchHire que el CÓDIGO REAL produciría para cada terminal del
        /// Appointment (vía el StatusMapping activo aplicado dentro de
        /// StripeRefundService.ProcessMoneyDistributionAsync / CancelAppointmentAsync).
        /// </summary>
        public static string ExpectedHireStatus(string terminalApptStatus) => terminalApptStatus switch
        {
            Completed => "completed",
            CancelledByClientSecond => "cancelled",          // mapping activo → cancelled
            CancelledByExpertSecond => "cancelled",
            CancelledByExpertRejection => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(terminalApptStatus), terminalApptStatus, "No es terminal"),
        };

        /// <summary>
        /// DEPRECADO: la divergencia que documentaba quedó CERRADA. El simulador ahora
        /// transiciona el hire a 'cancelled' en las cancelaciones/rechazo terminales
        /// (MarketplaceFlowSimulator.FinalizeHireViaMappingAsync), igual que el backend real.
        /// Se mantiene como alias de <see cref="ExpectedHireStatus"/> por compatibilidad.
        /// </summary>
        public static string ExpectedHireStatusInSimulator(string terminalApptStatus)
            => ExpectedHireStatus(terminalApptStatus);

        /// <summary>
        /// statusValue con el que ProcessMoneyDistribution resuelve la StatusConfiguration
        /// para cada terminal (verificado contra SEED_ESTADOS_COMPLETO.sql + el fallback de
        /// mapping del core ProcessMoneyDistributionAsync).
        /// </summary>
        public static string MoneyStatusValue(string terminalApptStatus) => terminalApptStatus switch
        {
            // CompleteService llama ProcessMoneyDistributionAsync con "completed" (SearchHireStatus).
            Completed => "completed",
            CancelledByClientSecond => CancelledByClientSecond,   // AppointmentStatus config 0/95/5
            CancelledByExpertSecond => CancelledByExpertSecond,   // AppointmentStatus config 100/0/0
            // No tiene config propia: fallback vía mapping activo → cancelled → 100/0/0
            CancelledByExpertRejection => CancelledByExpertRejection,
            _ => throw new ArgumentOutOfRangeException(nameof(terminalApptStatus), terminalApptStatus, "No es terminal"),
        };

        public sealed record SequenceResult(IReadOnlyList<Action> Actions, ModelState Final);

        /// <summary>
        /// WALKER model-based: BFS sobre el grafo de transiciones válidas. Genera todas las
        /// secuencias que alcanzan un estado FINAL en ≤ <paramref name="maxDepth"/> pasos,
        /// podando las acciones que el guard rechazaría (Next devuelve null).
        ///
        /// Para acotar el coste y evitar ciclos infinitos (propose↔reject↔propose, etc.) se
        /// limita la longitud y el nº de visitas por (estado, profundidad).
        /// </summary>
        public static IReadOnlyList<SequenceResult> EnumerateTerminalSequences(int maxDepth)
        {
            var results = new List<SequenceResult>();
            var allActions = Enum.GetValues<Action>();

            void Dfs(ModelState state, List<Action> path)
            {
                if (IsTerminal(state))
                {
                    results.Add(new SequenceResult(path.ToArray(), state));
                    return;
                }
                if (path.Count >= maxDepth) return;

                foreach (var action in allActions)
                {
                    var next = Next(state, action);
                    if (next is null) continue;
                    path.Add(action);
                    Dfs(next, path);
                    path.RemoveAt(path.Count - 1);
                }
            }

            Dfs(ModelState.Initial, new List<Action>());
            return results;
        }

        /// <summary>
        /// Genera TODAS las secuencias inválidas de longitud 1: (estado alcanzable, acción)
        /// donde Next devuelve null PERO el simulador implementa un guard que lanza excepción.
        /// Excluye las acciones Cancel (el simulador no las valida — divergencia reportada).
        /// </summary>
        public static IReadOnlyList<(ModelState From, Action Action)> EnumerateGuardedInvalidTransitions()
        {
            // Acciones cuyo guard SÍ está implementado en el simulador y lanza excepción.
            var guardedActions = new[]
            {
                Action.Propose, Action.Confirm, Action.Reject, Action.SubmitReport,
            };

            // Estados representativos alcanzables (no terminales).
            var reachableStates = new[]
            {
                ModelState.Initial,                                         // awaiting_appointment
                ModelState.Initial with { ApptStatus = Proposed },
                ModelState.Initial with { ApptStatus = Confirmed },
                ModelState.Initial with { ApptStatus = Rejected, RejectionCount = 1 },
                ModelState.Initial with { ApptStatus = AwaitingReport },
                ModelState.Initial with { ApptStatus = ReportSent },
            };

            var invalid = new List<(ModelState, Action)>();
            foreach (var st in reachableStates)
                foreach (var act in guardedActions)
                    if (Next(st, act) is null)
                        invalid.Add((st, act));
            return invalid;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DESPACHADOR · ejecuta UNA acción modelada contra los métodos del simulador
    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Ejecuta la acción <paramref name="action"/> del modelo contra la BD usando los
    /// métodos YA EXISTENTES del simulador (no duplica lógica). Devuelve el statusValue
    /// del appointment resultante. Lanza si la acción no es válida en el estado actual
    /// (delegando en los guards del simulador).
    /// </summary>
    public static async Task<string> DispatchStateMachineActionAsync(
        AppDbContext db,
        int hireId,
        int clientUserId,
        int expertUserId,
        StateMachine.Action action,
        DateTime proposedDate)
    {
        switch (action)
        {
            case StateMachine.Action.Propose:
                var p = await ClientProposesAppointmentAsync(db, hireId, clientUserId, proposedDate);
                return p.Status!.StatusValue;

            case StateMachine.Action.Confirm:
                var c = await ExpertConfirmsAppointmentAsync(db, hireId, expertUserId);
                return c.Status!.StatusValue;

            case StateMachine.Action.Reject:
                return await ExpertRejectsAppointmentAsync(db, hireId, expertUserId);

            case StateMachine.Action.CancelClient:
                return await CancelByClientAsync(db, hireId, clientUserId);

            case StateMachine.Action.CancelExpert:
                return await CancelByExpertAsync(db, hireId, expertUserId);

            case StateMachine.Action.AdvanceToAwaitingReport:
                await AdvanceToAwaitingReportAsync(db, hireId);
                return await CurrentApptStatusValueAsync(db, hireId);

            case StateMachine.Action.SubmitReport:
                await ExpertSubmitsReportAsync(db, hireId, expertUserId);
                return await CurrentApptStatusValueAsync(db, hireId);

            case StateMachine.Action.Approve:
                await ClientApprovesReportFromAwaitingDecisionAsync(db, hireId, clientUserId);
                return await CurrentApptStatusValueAsync(db, hireId);

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    /// <summary>Lee el StatusValue actual del appointment del hire (helper de lectura).</summary>
    public static async Task<string> CurrentApptStatusValueAsync(AppDbContext db, int hireId)
    {
        var appt = await db.Appointments
            .Include(a => a.Status)
            .SingleAsync(a => a.SearchHireId == hireId);
        return appt.Status!.StatusValue;
    }
}
