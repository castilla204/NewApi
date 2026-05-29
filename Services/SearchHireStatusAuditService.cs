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
    /// - No abre transacción propia: si el caller tiene una transacción activa, la fila
    ///   se inserta en ella; si falla SaveChanges del caller, la auditoría tampoco
    ///   queda huérfana (es lo deseado).
    /// - Best-effort: si falla la inserción del historial NO debe abortar la operación
    ///   principal (loguea critical y sigue). El estado canónico vive en SearchHire.StatusId.
    ///
    /// USO TÍPICO
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
    /// await _context.SaveChangesAsync();
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
