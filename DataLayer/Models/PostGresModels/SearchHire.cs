

namespace newApi.DataLayer.Models.PostGresModels
{
    public class SearchHire
    {
        public int Id { get; set; }
        public int? ClientId { get; set; } // ✅ Nullable para permitir anonimización completa en eliminación de cuentas
        public int? ExpertId { get; set; }
        public int SearchServiceId { get; set; }
        public int? SearchId { get; set; } // ✅ Nullable para permitir anonimización cuando se eliminan Searches
        public int StatusId { get; set; }
        public virtual SystemStatus Status { get; set; }
        public string? ExpertTransferId { get; set; }
        public decimal Amount { get; set; }
        
        /// <summary>
        /// Base amount sin IVA/tax (pre-tax). Se calcula desde Stripe Tax breakdown.
        /// Si es null, se usa Amount como fallback para compatibilidad con datos existentes.
        /// </summary>
        public decimal? BaseAmount { get; set; }
        
        /// <summary>
        /// Monto de IVA/tax calculado por Stripe Tax. Si es null o 0, no hay tax aplicado.
        /// </summary>
        public decimal? TaxAmount { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
        public DateTime? CompletionDeadline { get; set; }
        public bool? ClientApproved { get; set; }
        public virtual User? Client { get; set; } // ✅ Nullable para permitir anonimización completa en eliminación de cuentas
        public virtual User? Expert { get; set; }
        public virtual SearchService SearchService { get; set; }
        public virtual Search Search { get; set; }
        public virtual ICollection<Dispute> Disputes { get; set; }
        public virtual ICollection<Conversation> Conversations { get; set; } = new List<Conversation>();
        public virtual ICollection<SearchHireDeliverable> Deliverables { get; set; } = new List<SearchHireDeliverable>();
        public virtual Appointment? Appointment { get; set; } // Solo si requiere cita
        
        /// <summary>
        /// ID de la disponibilidad del experto usada al momento de crear esta contratación
        /// Permite mantener la disponibilidad original aunque el experto la haya cambiado
        /// </summary>
        public int? ExpertAvailabilityId { get; set; }
        public virtual ExpertAvailability? ExpertAvailability { get; set; }
        
        /// <summary>
        /// Zona horaria del experto al momento de crear esta contratación (formato IANA)
        /// CRÍTICO: Se guarda para que contrataciones activas mantengan el timezone original
        /// aunque el experto cambie su ubicación/timezone después
        /// </summary>
        public string? ExpertTimezone { get; set; }
        
        /// <summary>
        /// Código de país del experto al momento de crear esta contratación (ISO 3166-1 alpha-2)
        /// CRÍTICO: Se guarda para que contrataciones activas mantengan el país original
        /// aunque el experto cambie su ubicación después
        /// </summary>
        public string? ExpertCountry { get; set; }

        /// <summary>
        /// Flag operativo: el hire requiere revisión manual del admin (p.ej. cuenta de experto
        /// rechazada o desautorizada por Stripe tras prestar el servicio). NO se considera un
        /// estado canónico — el estado canónico sigue en StatusId.
        /// </summary>
        public bool RequiresManualReview { get; set; }

        /// <summary>
        /// Estado de captura para el flujo "outbox" (P1-5). Valores: "Pending", "Captured", "Failed", null.
        /// Cuando es null o "Captured", el hire es operable; cuando es "Pending" el watchdog Hangfire
        /// debe reintentar la captura. No reemplaza al StatusId, lo complementa.
        /// </summary>
        public string? CaptureStatus { get; set; }

        /// <summary>
        /// P3-1 (versión mínima): marca temporal del momento en que la Fase 3 de RefundService
        /// agotó todos los reintentos Hangfire sin lograr distribuir el dinero (transfer/refund).
        /// Sigue en estado Completed/DisputeResolvedClient pero queda visible para el admin digest
        /// diario. Null = no hay incidencia pendiente. La versión COMPLETA (RefundPending state +
        /// PendingFinalStatusId + reescritura de Fase 2) se documenta en RESUMEN_P3_PENDIENTES.md.
        /// </summary>
        public DateTime? RefundFailedAt { get; set; }
    }

}
