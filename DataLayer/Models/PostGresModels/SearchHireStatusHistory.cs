using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// 📜 Round 9 — A2 FIX: Audit log inmutable de cambios de estado de SearchHire.
    ///
    /// MOTIVACIÓN
    /// ----------
    /// Antes de este round, los cambios de SearchHire.StatusId se realizaban directamente
    /// con `SaveChangesAsync()` sin dejar rastro de:
    ///   - Quién (UserId del actor: cliente, experto, admin, sistema)
    ///   - Cuándo (timestamp UTC del cambio)
    ///   - Por qué (motivo / contexto)
    ///   - De qué estado (Old → New)
    ///   - Origen (HTTP endpoint, Hangfire job, webhook Stripe...)
    ///
    /// Esta ausencia rompía GDPR Art 5(1)(f) "integridad y confidencialidad" y Art 5(2)
    /// "accountability" — el responsable de tratamiento debe poder DEMOSTRAR el cumplimiento.
    /// También dificultaba el soporte (no se podía reconstruir el ciclo de vida de una hire
    /// para resolver disputas).
    ///
    /// DISEÑO
    /// ------
    /// Append-only: nunca se UPDATE ni DELETE filas. Cada cambio genera una nueva fila.
    /// Retención: 6 años (alineado con obligación fiscal española de conservar facturas).
    /// Acceso: solo Admin role + el cliente/experto sobre sus propias hires.
    ///
    /// USO
    /// ---
    /// Llamar a <c>SearchHireStatusAuditService.RecordTransitionAsync(...)</c> ANTES de
    /// `_context.SaveChangesAsync()` cuando se modifica `SearchHire.StatusId`. El servicio
    /// inserta la fila en la misma transacción para garantizar consistencia (si el commit
    /// falla, la auditoría no queda huérfana).
    /// </summary>
    [Table("SearchHireStatusHistory")]
    public class SearchHireStatusHistory
    {
        public int Id { get; set; }

        /// <summary>FK a SearchHire. NO nullable: si la hire se elimina, las filas de historial se borran en cascada.</summary>
        [Required]
        public int SearchHireId { get; set; }

        public virtual SearchHire? SearchHire { get; set; }

        /// <summary>StatusId anterior. Null sólo en la primera transición ("creación" → Pending).</summary>
        public int? OldStatusId { get; set; }

        /// <summary>Nuevo StatusId tras la transición. Siempre presente.</summary>
        [Required]
        public int NewStatusId { get; set; }

        /// <summary>
        /// UserId del actor que disparó el cambio. Null si fue el sistema (Hangfire job,
        /// watchdog, webhook sin contexto de usuario explícito).
        /// </summary>
        public int? ChangedByUserId { get; set; }

        /// <summary>
        /// Origen lógico del cambio: nombre del endpoint, job o handler que lo causó.
        /// Ej: "SubscriptionController.HandlePendingHireCompleted",
        ///     "RefundService.ProcessMoneyDistributionAsync",
        ///     "AppointmentService.OnTimerFiredAsync",
        ///     "DisputeController.ResolveDispute".
        /// Limitado a 200 chars (suficiente para Class.Method).
        /// </summary>
        [MaxLength(200)]
        public string? Source { get; set; }

        /// <summary>
        /// Motivo legible del cambio (ej: "Cliente aceptó la propuesta del experto",
        /// "Timer de 24h sin respuesta del cliente disparado", "Disputa resuelta a favor del cliente").
        /// </summary>
        [MaxLength(500)]
        public string? Reason { get; set; }

        /// <summary>
        /// Metadata extra en formato JSON serializado (ej: ChargeId, RefundId, TransferId,
        /// PaymentIntentId, DisputeId asociados). Útil para reconstruir contexto en disputas.
        /// </summary>
        public string? AdditionalDataJson { get; set; }

        /// <summary>Timestamp UTC del cambio. Inmutable; nunca se actualiza.</summary>
        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
