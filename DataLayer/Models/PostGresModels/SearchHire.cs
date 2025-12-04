

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
    }

}
