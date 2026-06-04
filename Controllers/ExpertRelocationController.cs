using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using newApi.Services;

namespace newApi.Controllers
{
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // 🛡️ Round 28 MUD-E + MUD-R: Wizard de mudanza self-service del experto.
    //
    // Stripe Connect Express prohíbe cambiar el `country` de una cuenta — la única
    // vía es cerrar la actual y abrir otra nueva. Estos endpoints orquestan eso
    // SIN duplicar User: preflight valida que no haya dinero en vuelo, execute
    // realiza el cierre + reseteo + flag de mudanza. El nuevo onboarding lo hace
    // el experto desde /become-expert (que ya detecta RelocatedFromCountry != null).
    //
    // 🛡️ MUD-R (GAP-DESCUBRIMIENTO): este controller vive en su propio archivo
    // (no como nested class dentro de UserController) porque ASP.NET Core ignora
    // tipos anidados — el feature provider filtra por TypeInfo.IsPublic que es
    // false para tipos nested (incluso si llevan modificador `public`). El bug se
    // manifestaba como 404 silencioso sin warnings en build.
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    [Route("api/User/expert-relocation")]
    [ApiController]
    [EnableRateLimiting("api")]
    public class ExpertRelocationController : ControllerBase
    {
        private readonly ExpertRelocationService _service;
        private readonly ILoggingService _loggingService;

        public ExpertRelocationController(
            ExpertRelocationService service,
            ILoggingService loggingService)
        {
            _service = service;
            _loggingService = loggingService;
        }

        /// <summary>
        /// Devuelve si el experto puede mudarse + razones de bloqueo si las hay.
        /// Frontend muestra esto antes de pintar el formulario de mudanza.
        /// </summary>
        [Authorize]
        [HttpGet("preflight")]
        public async Task<IActionResult> Preflight(CancellationToken ct)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(userIdStr, out var userId))
                return Unauthorized(new { error = "Invalid token" });

            var result = await _service.PreflightAsync(userId, ct);

            // 🛡️ Round 28 MUD-S: Program.cs:826 fuerza PropertyNamingPolicy = null, así
            // que las propiedades PascalCase de RelocationPreflightResult se serializarían
            // como PascalCase. El frontend (useExpertRelocation.ts) tipa la respuesta como
            // camelCase, así que mapeamos explícitamente aquí — mismo patrón que
            // CurrenciesController. Sin este mapeo, el wizard recibe undefined en todos
            // los campos camelCase y muestra el step "blocked" vacío.
            return Ok(new
            {
                canProceed = result.CanProceed,
                blockedReason = result.BlockedReason,
                pendingDisputes = result.PendingDisputes,
                activeHires = result.ActiveHires,
                recentRefunds = result.RecentRefunds,
                receivedReviewsCount = result.ReceivedReviewsCount,
                activeServicesCount = result.ActiveServicesCount,
                currentCountry = result.CurrentCountry,
                stripeAccountId = result.StripeAccountId,
            });
        }

        /// <summary>
        /// Ejecuta la mudanza: cierra Stripe acct, marca el ExpertProfile como
        /// `RelocatedFromCountry` + resetea OnboardingCompleted=false. El experto
        /// continúa con onboarding nuevo desde /become-expert.
        /// </summary>
        [Authorize]
        [HttpPost("execute")]
        public async Task<IActionResult> Execute([FromBody] ExpertRelocationExecuteRequest body, CancellationToken ct)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(userIdStr, out var userId))
                return Unauthorized(new { error = "Invalid token" });

            if (body == null || string.IsNullOrWhiteSpace(body.ConfirmationPhrase))
                return BadRequest(new { error = "MISSING_CONFIRMATION", message = "Frase de confirmación requerida." });

            if (!string.Equals(body.ConfirmationPhrase.Trim(), "MUDARME", System.StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "INVALID_CONFIRMATION", message = "Escribe 'MUDARME' exactamente para confirmar." });

            var (success, errorMessage, stripeOps) = await _service.ExecuteAsync(userId, body.Reason, force: false, ct);
            if (!success)
                return Conflict(new { error = "BLOCKED", message = errorMessage });

            return Ok(new
            {
                success = true,
                message = "Cuenta cerrada. Ve a 'Convertirse en experto' para iniciar el onboarding en tu nuevo país.",
                nextStep = "/become-expert",
                stripeOps,
            });
        }
    }

    public class ExpertRelocationExecuteRequest
    {
        /// <summary>Frase de confirmación obligatoria — debe ser literal "MUDARME".</summary>
        public string? ConfirmationPhrase { get; set; }

        /// <summary>Motivo opcional para auditoría.</summary>
        public string? Reason { get; set; }

        /// <summary>País nuevo (informativo — el país real se setea en re-onboarding).</summary>
        public string? NewCountry { get; set; }
    }
}
