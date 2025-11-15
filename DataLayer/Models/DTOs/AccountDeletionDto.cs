using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para solicitar el borrado de una cuenta
    /// </summary>
    public class AccountDeletionRequestDto
    {
        public string? Reason { get; set; } = null; // Opcional para usuarios que se eliminan a sí mismos
    }

    /// <summary>
    /// DTO para la respuesta del borrado de cuenta
    /// </summary>
    public class AccountDeletionResponseDto
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<ActiveContractInfo> ActiveContracts { get; set; } = new List<ActiveContractInfo>();
        public List<DisputeCreatedInfo> DisputesCreated { get; set; } = new List<DisputeCreatedInfo>();
        public bool RequiresManualReview { get; set; }
        // ✅ MEJORA: IDs de contrataciones que fallaron para facilitar revisión manual
        public List<int> FailedSearchHireIds { get; set; } = new List<int>();
        // ✅ MEJORA: Cantidad de contrataciones que fallaron
        public int FailedContractsCount { get; set; }
    }

    /// <summary>
    /// Información sobre contrataciones activas
    /// </summary>
    public class ActiveContractInfo
    {
        public int SearchHireId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string ServiceName { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; }
        public string OtherPartyName { get; set; } = string.Empty;
        public string OtherPartyEmail { get; set; } = string.Empty;
        public bool HasAppointment { get; set; }
        public DateTime? AppointmentDate { get; set; }
    }

    /// <summary>
    /// Información sobre disputas creadas automáticamente
    /// </summary>
    public class DisputeCreatedInfo
    {
        public int DisputeId { get; set; }
        public int SearchHireId { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string AffectedPartyName { get; set; } = string.Empty;
        public string AffectedPartyEmail { get; set; } = string.Empty;
    }

    /// <summary>
    /// DTO para verificar el estado de borrado de cuenta
    /// </summary>
    public class AccountDeletionStatusDto
    {
        public bool CanDeleteImmediately { get; set; }
        public bool HasActiveContracts { get; set; }
        public int ActiveContractsCount { get; set; }
        public List<ActiveContractInfo> ActiveContracts { get; set; } = new List<ActiveContractInfo>();
        public string Message { get; set; } = string.Empty;
    }
}
