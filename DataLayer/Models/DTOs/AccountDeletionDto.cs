using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para solicitar el borrado de una cuenta
    /// </summary>
    public class AccountDeletionRequestDto
    {
        public string? Reason { get; set; } = null; // Opcional para usuarios que se eliminan a sí mismos

        // 🛡️ SEC-1 — Reautenticación obligatoria para una acción destructiva e irreversible.
        // El usuario debe demostrar posesión de la cuenta, no solo tener un JWT válido.
        //  - Cuentas con contraseña: enviar Password (se verifica con BCrypt).
        //  - Cuentas OAuth (Google/Apple, sin contraseña): enviar VerificationToken + Code,
        //    obtenidos de POST /api/AccountDeletion/request-otp (OTP step-up por email).
        // El endpoint admin (admin/delete/{userId}) NO requiere estos campos.
        public string? Password { get; set; } = null;
        public string? VerificationToken { get; set; } = null;
        public string? Code { get; set; } = null;
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
        /// <summary>
        /// 🛡️ Round 28 — Sprint 3: divisa del hire (ISO 4217 MAYÚSCULAS). Frontend la usa para
        /// formatear Amount con el símbolo correcto en AccountSettingsModal y AccountDeletionModal.
        /// Default "EUR" para retro-compat con flujos legacy que no setean este campo.
        /// </summary>
        public string Currency { get; set; } = "EUR";
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
