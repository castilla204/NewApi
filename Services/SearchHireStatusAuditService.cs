using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    /// <summary>
    /// 📜 Round 9 — A2 FIX: servicio que registra transiciones de estado de SearchHire
    /// en la tabla append-only <see cref="SearchHireStatusHistory"/>.
    ///
    /// CONTRATO
    /// --------
    /// - Idempotente a nivel de actor: el caller debe llamar UNA vez por transición.
    /// - **ATOMICIDAD CON LA MUTACIÓN DE ESTADO**: este servicio NO llama a SaveChangesAsync
    ///   por su cuenta. Sólo hace _context.SearchHireStatusHistories.AddAsync(...). Esto
    ///   significa que:
    ///     * El caller DEBE mutar hire.StatusId y luego llamar a UN ÚNICO SaveChangesAsync
    ///       que persistirá ambas cosas (mutación + auditoría) en la misma transacción.
    ///     * Si ese SaveChangesAsync falla (o se hace rollback), la fila de auditoría se
    ///       descarta junto con el cambio de estado: NO queda huérfana, NO queda en
    ///       estado inconsistente.
    ///     * NO es asíncrono/diferido: la auditoría no se "envía a una cola"; vive y muere
    ///       con el SaveChangesAsync del caller. No reutilizar este patrón asumiendo lo
    ///       contrario.
    /// - Best-effort frente a fallos del propio servicio: si la construcción del entry o
    ///   la serialización de additionalData fallan, NO se aborta la operación principal
    ///   (loguea critical y sigue). El estado canónico vive en SearchHire.StatusId.
    ///
    /// USO TÍPICO (correcto — mutación y auditoría atómicas en un SaveChangesAsync):
    /// ----------
    /// <code>
    /// var oldStatus = hire.StatusId;
    /// hire.StatusId = newStatusId;
    /// hire.UpdatedAt = DateTime.UtcNow;
    /// await _statusAudit.RecordTransitionAsync(
    ///     hire.Id, oldStatus, newStatusId,
    ///     changedByUserId: actorId,
    ///     source: "SubscriptionController.HandlePendingHireCompleted",
    ///     reason: "Cliente aceptó la propuesta del experto",
    ///     additionalData: new { PaymentIntentId = pi.Id });
    /// await _context.SaveChangesAsync(); // <-- persiste mutación + auditoría juntas
    /// </code>
    ///
    /// ANTI-PATRÓN (NO HACER):
    /// ----------
    /// <code>
    /// hire.StatusId = newStatusId;
    /// await _context.SaveChangesAsync();              // <-- estado persistido SIN auditoría
    /// await _statusAudit.RecordTransitionAsync(...);  // <-- auditoría queda sin commit
    /// // Si el proceso crashea aquí, hay estado nuevo SIN su entrada de historial.
    /// </code>
    /// </summary>
    public interface ISearchHireStatusAuditService
    {
        Task RecordTransitionAsync(
            int searchHireId,
            int? oldStatusId,
            int newStatusId,
            int? changedByUserId = null,
            string? source = null,
            string? reason = null,
            object? additionalData = null);
    }

    public class SearchHireStatusAuditService : ISearchHireStatusAuditService
    {
        private readonly AppDbContext _context;
        private readonly ILoggingService _loggingService;

        public SearchHireStatusAuditService(AppDbContext context, ILoggingService loggingService)
        {
            _context = context;
            _loggingService = loggingService;
        }

        public async Task RecordTransitionAsync(
            int searchHireId,
            int? oldStatusId,
            int newStatusId,
            int? changedByUserId = null,
            string? source = null,
            string? reason = null,
            object? additionalData = null)
        {
            // Precondición: searchHireId debe ser un id válido (> 0). Si es 0 o negativo
            // significa que el caller pasó una variable no inicializada o un valor inválido;
            // la inserción fallaría por FK contra SearchHires y la excepción quedaría
            // silenciada en el catch best-effort. Detectamos y logueamos explícitamente
            // SIN abortar la operación principal (consistente con el contrato best-effort).
            if (searchHireId <= 0)
            {
                try
                {
                    await _loggingService.LogCriticalAsync(
                        message: "A2 FIX: RecordTransitionAsync invocado con searchHireId inválido",
                        details: $"searchHireId={searchHireId} (debe ser > 0). Transición {oldStatusId} → {newStatusId} NO se registró. Probable bug en el caller (variable no inicializada).",
                        userId: changedByUserId,
                        source: "SearchHireStatusAuditService.RecordTransitionAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { OldStatusId = oldStatusId, NewStatusId = newStatusId, Source = source });
                }
                catch { /* swallow */ }
                return;
            }

            try
            {
                string? additionalDataJson = null;
                if (additionalData != null)
                {
                    try
                    {
                        additionalDataJson = JsonSerializer.Serialize(additionalData);
                        // Recortar si excede límite razonable (BD permite text, pero evitamos abusos)
                        if (additionalDataJson.Length > 4000)
                        {
                            additionalDataJson = additionalDataJson.Substring(0, 4000) + "...[truncated]";
                        }
                    }
                    catch
                    {
                        // Si la serialización falla, no abortar — solo dejar contexto mínimo
                        additionalDataJson = $"<unable to serialize: {additionalData.GetType().Name}>";
                    }
                }

                var entry = new SearchHireStatusHistory
                {
                    SearchHireId = searchHireId,
                    OldStatusId = oldStatusId,
                    NewStatusId = newStatusId,
                    ChangedByUserId = changedByUserId,
                    Source = string.IsNullOrEmpty(source) ? null : (source.Length > 200 ? source.Substring(0, 200) : source),
                    Reason = string.IsNullOrEmpty(reason) ? null : (reason.Length > 500 ? reason.Substring(0, 500) : reason),
                    AdditionalDataJson = additionalDataJson,
                    CreatedAt = DateTime.UtcNow
                };

                await _context.SearchHireStatusHistories.AddAsync(entry);
                // OJO: NO llamamos SaveChangesAsync aquí. El caller lo hace en su transacción.
            }
            catch (Exception ex)
            {
                // Best-effort: nunca abortar la operación principal por fallo de auditoría.
                try
                {
                    await _loggingService.LogCriticalAsync(
                        message: "A2 FIX: Falló registro de SearchHireStatusHistory",
                        details: $"SearchHire {searchHireId}: {oldStatusId} → {newStatusId}. Exception: {ex.Message}",
                        userId: changedByUserId,
                        source: "SearchHireStatusAuditService.RecordTransitionAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { OldStatusId = oldStatusId, NewStatusId = newStatusId, Source = source });
                }
                catch { /* swallow */ }
            }
        }
    }
}
