using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace newApi.Services
{
    /// <summary>
    /// 🛡️ Round 28 MUD-BD: helper para insertar filas en ClawbackQueues cuando se detecta
    /// una pérdida no recuperable automáticamente. Best-effort: si la persistencia falla,
    /// loggeamos warning y seguimos — el log Critical original sigue siendo la fuente de
    /// verdad para el admin.
    /// </summary>
    public class ClawbackQueueService
    {
        private readonly AppDbContext _context;
        private readonly ILoggingService _loggingService;

        public ClawbackQueueService(AppDbContext context, ILoggingService loggingService)
        {
            _context = context;
            _loggingService = loggingService;
        }

        // ✅ FIX AUDITORÍA [M3] Medium: cola write-only sin drenaje. EnqueueAsync solo Add+SaveChanges;
        // ningún RecurringJob de Program.cs (líneas 1966-2124) drena/reintenta/re-alerta esta cola.
        // El único lector es AdminController.ListClawbackQueue/ResolveClawback (pull manual, no mueve dinero).
        // Disparo: borrado GDPR con Pending balance (AccountDeletionService.cs:2602) o chargeback con
        // balance retirado (RefundService.cs:2874) encolan filas que quedan ResolvedAt==null para siempre
        // salvo que un admin abra el dashboard → deuda experto↔plataforma (incl. devoluciones GDPR) sin acotar.
        // FIX: SweepPendingClawbacksAsync (abajo) re-alerta filas pendientes con antigüedad > umbral
        // (Critical agregado por divisa/motivo) y debe registrarse como RecurringJob (ver needsWiring).
        public async Task EnqueueAsync(
            int userId,
            string? stripeAccountId,
            decimal amountMajor,
            string currency,
            string reason,
            string? notes = null,
            int? searchHireId = null,
            CancellationToken ct = default)
        {
            var entity = new ClawbackQueue
            {
                UserId = userId,
                StripeAccountId = stripeAccountId,
                SearchHireId = searchHireId,
                AmountMajor = amountMajor,
                Currency = (currency ?? "EUR").ToUpperInvariant(),
                Reason = reason,
                Notes = notes,
                CreatedAt = DateTime.UtcNow
            };
            try
            {
                _context.ClawbackQueues.Add(entity);
                // ⚠️ AUDITORÍA [M3] Medium: este es el ÚNICO punto que persiste la deuda; tras él no hay
                // proceso que vuelva a tocar la fila. Si falla, no hay reintento automático posterior.
                // Fix: emparejar con un job de barrido que lea ClawbackQueues.Where(ResolvedAt==null).
                await _context.SaveChangesAsync(ct);
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
            {
                // Tabla ClawbackQueues inexistente: no es un fallo transitorio, es pérdida de
                // la pista de auditoría de dinero. Critical, no Warning.
                DetachFailedEntity(entity);
                await _loggingService.LogCriticalAsync(
                    // ⚠️ AUDITORÍA [M3] Medium: rama 42P01 (tabla inexistente) NO persiste la fila — solo
                    // queda este log Critical, sin entrada en el dashboard. Escenario real por drift del
                    // historial EF (migración R28_MUD_BD_ClawbackQueues usa CREATE TABLE IF NOT EXISTS).
                    // Disparo: invocar EnqueueAsync cuando el esquema físico no tiene ClawbackQueues →
                    // pérdida total de la pista de auditoría de dinero (deuda no rastreable jamás).
                    // Fix: garantizar creación de la tabla en boot (ensure-schema) o persistir un fallback
                    // duradero (p.ej. FinancialTransaction marker) además del log.
                    message: "MUD-BD: ClawbackQueues table missing — clawback row NOT persisted",
                    details: $"UserId {userId} acct {stripeAccountId} amount {amountMajor:F2} {currency} reason={reason}: tabla inexistente (42P01): {ex.Message}. El log Critical previo sigue siendo la alerta admin, pero falta la fila de seguimiento.",
                    userId: userId,
                    source: "ClawbackQueueService.EnqueueAsync",
                    relatedEntityType: "ClawbackQueue");
            }
            catch (Exception ex)
            {
                // No bloqueamos el flow del caller — el Critical log adyacente ya alerta al admin.
                DetachFailedEntity(entity);
                await _loggingService.LogWarningAsync(
                    message: "MUD-BD: failed to enqueue clawback row (non-blocking)",
                    details: $"UserId {userId} acct {stripeAccountId} amount {amountMajor:F2} {currency} reason={reason}: persist falló: {ex.Message}. El log Critical previo sigue siendo la alerta admin.",
                    userId: userId,
                    source: "ClawbackQueueService.EnqueueAsync",
                    relatedEntityType: "ClawbackQueue");
            }
        }

        // ✅ FIX AUDITORÍA [M3] Medium: drenaje/re-alerta de la cola. Antes nada tocaba las filas
        // ResolvedAt==null tras EnqueueAsync → la deuda experto↔plataforma envejecía indefinidamente
        // salvo que un admin abriese el dashboard. Este barrido debe registrarse como RecurringJob
        // (diario o cada 6h; ver needsWiring) para que ops reciba una alerta escalada sin pull manual.
        private const int SweepStaleThresholdHours = 24;
        private const int SweepMaxBatch = 500;

        /// <summary>
        /// 🛡️ Round 28 MUD-BD: barrido de la cola de clawbacks. Lee filas con ResolvedAt==null,
        /// y si hay pendientes que superan el umbral de antigüedad emite UNA re-alerta Critical
        /// agregada (por divisa y motivo, con recuento total) vía ILoggingService, para que ops no
        /// dependa de mirar el dashboard. NO mueve dinero: la resolución sigue siendo manual.
        /// Idempotente respecto a dinero (solo lee + loguea); se alerta una vez por ejecución, no por fila.
        /// Pensado para registrarse como RecurringJob (diario o cada 6h).
        /// </summary>
        public async Task SweepPendingClawbacksAsync()
        {
            try
            {
                var now = DateTime.UtcNow;
                var staleBefore = now.AddHours(-SweepStaleThresholdHours);

                // Lote acotado: ordenamos por las más antiguas primero para no perder de vista deuda vieja.
                var pending = await _context.ClawbackQueues
                    .Where(c => c.ResolvedAt == null)
                    .OrderBy(c => c.CreatedAt)
                    .Take(SweepMaxBatch)
                    .ToListAsync();

                if (pending.Count == 0)
                {
                    return; // Nada pendiente → no spameamos a ops.
                }

                // Solo escalamos a Critical si hay deuda VIEJA (> umbral). Lo reciente queda en el dashboard.
                var stale = pending.Where(c => c.CreatedAt < staleBefore).ToList();
                if (stale.Count == 0)
                {
                    return;
                }

                // Resumen agregado por (divisa, motivo) para que la alerta sea legible y accionable.
                var groups = stale
                    .GroupBy(c => new
                    {
                        Currency = (c.Currency ?? "EUR").ToUpperInvariant(),
                        Reason = string.IsNullOrWhiteSpace(c.Reason) ? "(unknown)" : c.Reason
                    })
                    .OrderByDescending(g => g.Sum(x => x.AmountMajor))
                    .ToList();

                var sb = new StringBuilder();
                foreach (var g in groups)
                {
                    var sum = g.Sum(x => x.AmountMajor);
                    var oldest = g.Min(x => x.CreatedAt);
                    var ageDays = Math.Max(0, (now - oldest).TotalDays);
                    sb.Append($"[{g.Key.Reason}/{g.Key.Currency}] count={g.Count()} total={sum:F2} oldest={oldest:yyyy-MM-dd}({ageDays:F1}d); ");
                }

                var batchCapped = pending.Count >= SweepMaxBatch;
                var staleTotalByCurrency = string.Join(", ",
                    stale.GroupBy(c => (c.Currency ?? "EUR").ToUpperInvariant())
                         .OrderBy(g => g.Key)
                         .Select(g => $"{g.Sum(x => x.AmountMajor):F2} {g.Key}"));

                // ✅ FIX AUDITORÍA [M3] Medium: re-alerta ESCALADA y AGREGADA (una por barrido, no por fila),
                // para que la deuda pendiente no dependa de que un admin abra el dashboard.
                // Hook reintento (NO automatizado aquí): los motivos reintentables (p.ej. "InFlightTransfer")
                // podrían reprocesarse en el futuro, PERO mover dinero requiere guardas por StripeId/clave de
                // idempotencia y revisión; este barrido se limita a observar + alertar (conservador).
                await _loggingService.LogCriticalAsync(
                    message: $"MUD-BD: {stale.Count} clawback(s) pendientes >{SweepStaleThresholdHours}h sin resolver (deuda experto↔plataforma envejeciendo)",
                    details: $"Total pendiente escalado: {staleTotalByCurrency}. Filas leídas en este barrido (ResolvedAt==null): {pending.Count}{(batchCapped ? $" (TOPE de lote {SweepMaxBatch} alcanzado — puede haber más)" : "")}. Desglose por motivo/divisa: {sb.ToString().TrimEnd(' ', ';')}. Resolver vía panel admin (GET /api/admin/clawback-queue) — este barrido NO mueve dinero.",
                    source: "ClawbackQueueService.SweepPendingClawbacksAsync",
                    relatedEntityType: "ClawbackQueue");
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
            {
                // Mismo tratamiento que EnqueueAsync: tabla inexistente → Critical, sin romper el job.
                await _loggingService.LogCriticalAsync(
                    message: "MUD-BD: ClawbackQueues table missing — sweep no pudo leer la cola",
                    details: $"Barrido de clawbacks abortado: tabla inexistente (42P01): {ex.Message}. La deuda pendiente (si la hay) no es rastreable hasta que el esquema tenga ClawbackQueues.",
                    source: "ClawbackQueueService.SweepPendingClawbacksAsync",
                    relatedEntityType: "ClawbackQueue");
            }
            catch (Exception ex)
            {
                // Best-effort: un fallo del barrido no debe tumbar el RecurringJob ni enmascararse en silencio.
                await _loggingService.LogWarningAsync(
                    message: "MUD-BD: sweep de clawbacks falló (no bloqueante)",
                    details: $"Error durante SweepPendingClawbacksAsync: {ex.Message}. Reintentará en la siguiente ejecución del RecurringJob.",
                    source: "ClawbackQueueService.SweepPendingClawbacksAsync",
                    relatedEntityType: "ClawbackQueue");
            }
        }

        /// <summary>
        /// Despega del change tracker la entidad que no se pudo persistir, para que un
        /// SaveChanges posterior del caller (que comparte este AppDbContext) no la reintente
        /// ni aborte por su culpa.
        /// </summary>
        private void DetachFailedEntity(ClawbackQueue entity)
        {
            try
            {
                var entry = _context.Entry(entity);
                if (entry.State == EntityState.Added)
                {
                    entry.State = EntityState.Detached;
                }
            }
            catch
            {
                // Best-effort: si el detach falla no debe enmascarar el error original.
            }
        }
    }
}
