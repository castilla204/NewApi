using Microsoft.Extensions.Configuration;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace newApi.Services
{
    /// <summary>
    /// 📱 SMS-CENTRAL (2026-06-12): envío de SMS transaccionales vía Twilio.
    ///
    /// Gated por configuración: si faltan AccountSid/AuthToken o no hay emisor
    /// (FromNumber o MessagingServiceSid), el servicio queda en modo NO-OP (loguea
    /// pero no envía). Así el código vive desplegado sin riesgo y se ACTIVA solo al
    /// poner los secrets en Render — sin tocar nada más.
    ///
    /// Best-effort por diseño: un fallo de Twilio nunca rompe el flujo de negocio
    /// (las notificaciones in-app + email siguen siendo el canal principal). El SMS
    /// es un refuerzo para acciones importantes (te han contratado, aprueba la cita…).
    /// </summary>
    public interface ISmsService
    {
        /// <summary>True si hay credenciales + emisor configurados (envía de verdad).</summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Envía un SMS. Devuelve true si Twilio lo aceptó. Best-effort: nunca lanza
        /// (los errores se loguean y devuelven false). No-op si IsEnabled es false.
        /// </summary>
        Task<bool> SendSmsAsync(string toPhoneNumber, string message);
    }

    public class SmsService : ISmsService
    {
        private readonly string _accountSid;
        private readonly string _authToken;
        private readonly string _fromNumber;
        private readonly string _messagingServiceSid;
        private readonly ILoggingService _logging;

        public SmsService(IConfiguration configuration, ILoggingService logging)
        {
            _accountSid = configuration["Twilio:AccountSid"] ?? "";
            _authToken = configuration["Twilio:AuthToken"] ?? "";
            _fromNumber = configuration["Twilio:FromNumber"] ?? "";
            _messagingServiceSid = configuration["Twilio:MessagingServiceSid"] ?? "";
            _logging = logging;
        }

        public bool IsEnabled =>
            !string.IsNullOrWhiteSpace(_accountSid)
            && !string.IsNullOrWhiteSpace(_authToken)
            && (!string.IsNullOrWhiteSpace(_fromNumber) || !string.IsNullOrWhiteSpace(_messagingServiceSid));

        public async Task<bool> SendSmsAsync(string toPhoneNumber, string message)
        {
            if (string.IsNullOrWhiteSpace(toPhoneNumber) || string.IsNullOrWhiteSpace(message))
                return false;

            if (!IsEnabled)
            {
                // Modo no-op: dejamos rastro para depurar sin romper nada.
                Console.WriteLine($"[SMS-CENTRAL] (no-op, sin credenciales/emisor) SMS a {Mask(toPhoneNumber)}: {Truncate(message, 60)}");
                return false;
            }

            try
            {
                TwilioClient.Init(_accountSid, _authToken);

                var options = new CreateMessageOptions(new PhoneNumber(toPhoneNumber))
                {
                    Body = message,
                };
                // Preferimos Messaging Service SID (pool de números, mejor entregabilidad);
                // si no, número emisor directo.
                if (!string.IsNullOrWhiteSpace(_messagingServiceSid))
                    options.MessagingServiceSid = _messagingServiceSid;
                else
                    options.From = new PhoneNumber(_fromNumber);

                var result = await MessageResource.CreateAsync(options);

                // Twilio acepta el mensaje en estado queued/accepted/sending/sent.
                var status = result?.Status?.ToString()?.ToLowerInvariant() ?? "";
                var accepted = status is "queued" or "accepted" or "sending" or "sent" or "delivered";
                if (!accepted)
                {
                    await _logging.LogWarningAsync(
                        message: "SMS no aceptado por Twilio",
                        details: $"To={Mask(toPhoneNumber)}, Status={status}, ErrorCode={result?.ErrorCode}, ErrorMessage={result?.ErrorMessage}",
                        source: "SmsService.SendSmsAsync");
                }
                return accepted;
            }
            catch (Exception ex)
            {
                // Best-effort: no propagar — el canal in-app/email ya cubre.
                await _logging.LogWarningAsync(
                    message: "Fallo enviando SMS (no crítico; canal in-app/email sigue activo)",
                    details: $"To={Mask(toPhoneNumber)}, Error={ex.Message}",
                    source: "SmsService.SendSmsAsync");
                return false;
            }
        }

        private static string Mask(string phone)
        {
            if (string.IsNullOrEmpty(phone) || phone.Length < 4) return "***";
            return new string('*', Math.Max(0, phone.Length - 4)) + phone[^4..];
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
