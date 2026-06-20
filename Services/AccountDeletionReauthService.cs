using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    /// <summary>
    /// 🛡️ SEC-1 — Reautenticación previa al borrado de cuenta.
    ///
    /// El borrado de cuenta es destructivo, irreversible y dispara movimientos de dinero
    /// (cancelación/transferencia de contratos activos). Tener un JWT válido NO basta: un
    /// token robado (XSS, sesión abierta en un dispositivo ajeno) podría destruir la cuenta.
    /// Antes de la auditoría, el modal pedía contraseña pero NUNCA se enviaba ni se verificaba.
    ///
    /// Este servicio exige prueba de posesión real:
    ///  - Cuenta con contraseña (User.Password != null) → verifica la contraseña con BCrypt.
    ///  - Cuenta OAuth (Google/Apple, User.Password == null) → verifica un OTP step-up enviado
    ///    por email (Purpose=StepUp) emitido en POST /api/AccountDeletion/request-otp.
    ///
    /// Pure-by-design (recibe el <see cref="User"/> ya cargado) para que sea unit-testeable sin BD.
    /// </summary>
    public record AccountDeletionReauthResult(bool Success, string? ErrorMessage = null);

    public interface IAccountDeletionReauthService
    {
        Task<AccountDeletionReauthResult> VerifyAsync(
            User user,
            AccountDeletionRequestDto request,
            string? ip,
            CancellationToken ct = default);
    }

    /// <inheritdoc />
    public class AccountDeletionReauthService : IAccountDeletionReauthService
    {
        private readonly IPasswordHashingService _passwordHasher;
        private readonly IEmailVerificationService _emailVerification;

        public AccountDeletionReauthService(
            IPasswordHashingService passwordHasher,
            IEmailVerificationService emailVerification)
        {
            _passwordHasher = passwordHasher;
            _emailVerification = emailVerification;
        }

        /// <inheritdoc />
        public async Task<AccountDeletionReauthResult> VerifyAsync(
            User user,
            AccountDeletionRequestDto request,
            string? ip,
            CancellationToken ct = default)
        {
            if (user == null)
                return new AccountDeletionReauthResult(false, "Usuario no encontrado.");
            if (request == null)
                return new AccountDeletionReauthResult(false, "Solicitud inválida.");

            // ── Cuenta con contraseña → verificar contraseña ──────────────────────────
            if (!string.IsNullOrEmpty(user.Password))
            {
                if (string.IsNullOrWhiteSpace(request.Password))
                    return new AccountDeletionReauthResult(false, "Debes confirmar tu contraseña para eliminar la cuenta.");

                if (!_passwordHasher.Verify(request.Password, user.Password))
                    return new AccountDeletionReauthResult(false, "La contraseña no es correcta.");

                return new AccountDeletionReauthResult(true);
            }

            // ── Cuenta OAuth (sin contraseña) → verificar OTP step-up por email ────────
            if (string.IsNullOrWhiteSpace(request.VerificationToken) || string.IsNullOrWhiteSpace(request.Code))
                return new AccountDeletionReauthResult(false, "Debes introducir el código de verificación enviado a tu email.");

            var verify = await _emailVerification.VerifyAsync(request.VerificationToken, request.Code, ip, ct);

            if (!verify.Success)
                return new AccountDeletionReauthResult(false, verify.ErrorMessage ?? "El código de verificación no es válido o ha expirado.");

            // Defensa en profundidad: el OTP debe ser de propósito StepUp y de ESTA cuenta,
            // para que un código emitido en otro flujo (reset/registro) u otra cuenta no sirva.
            if (verify.Purpose != EmailVerificationPurpose.StepUp)
                return new AccountDeletionReauthResult(false, "Código de verificación no válido para esta acción.");

            if (verify.UserId != user.Id)
                return new AccountDeletionReauthResult(false, "El código de verificación no corresponde a tu cuenta.");

            return new AccountDeletionReauthResult(true);
        }
    }
}
