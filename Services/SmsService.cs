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
                // Modo no-op: falta el EMISOR Twilio (Twilio:FromNumber o Twilio:MessagingServiceSid).
                // 🛡️ Antes esto solo hacía Console.WriteLine → invisible en el logging persistente, por eso
                // "el SMS no llega y no hay rastro". Ahora lo registramos como warning para que el modo
                // degradado sea diagnosticable. El canal email/in-app sigue cubriendo la notificación.
                await _logging.LogWarningAsync(
                    message: "SMS no enviado: Twilio sin emisor configurado (modo no-op)",
                    details: $"Falta Twilio:FromNumber o Twilio:MessagingServiceSid (AccountSid set={!string.IsNullOrWhiteSpace(_accountSid)}, " +
                             $"AuthToken set={!string.IsNullOrWhiteSpace(_authToken)}). To={Mask(toPhoneNumber)}, msg={Truncate(message, 60)}. " +
                             "Configura twilio-from-number (o twilio-messaging-service-sid) para activar el envío real.",
                    source: "SmsService.SendSmsAsync.NoOp");
                return false;
            }

            // 📱 Normalizar a E.164 en el chokepoint de envío: Twilio rechaza números nacionales
            // ("681634037" → error 21211). El número (p.ej. del vendedor) puede llegar en crudo porque
            // la metadata del webhook lo persiste sin normalizar. Preserva números ya internacionales (+..).
            var normalizedTo = PhoneLookupService.NormalizeToE164(toPhoneNumber, "ES");
            if (string.IsNullOrEmpty(normalizedTo))
            {
                await _logging.LogWarningAsync(
                    message: "SMS no enviado: número no normalizable a E.164",
                    details: $"To={Mask(toPhoneNumber)} no se pudo convertir a formato internacional (+34...). Se omite el envío.",
                    source: "SmsService.SendSmsAsync");
                return false;
            }

            try
            {
                TwilioClient.Init(_accountSid, _authToken);

                // 📱 GSM-7 single-segment: los acentos agudos (á í ó ú) y otros caracteres fuera
                // del alfabeto GSM 03.38 fuerzan codificación UCS-2, que baja el segmento de 160 a
                // 70 caracteres y parte el SMS en varios trozos. Multi-segmento = más filtrado de
                // carriers y riesgo de que la URL se rompa entre segmentos y deje de ser clicable.
                // Transliteramos a ASCII en el chokepoint de envío (mismo patrón que NormalizeToE164)
                // para mantener 1 segmento y máxima entregabilidad del enlace.
                var options = new CreateMessageOptions(new PhoneNumber(normalizedTo))
                {
                    Body = NormalizeForGsm7(message),
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

        /// <summary>
        /// Pliega los diacríticos latinos a ASCII (á→a, í→i, ó→o, ñ→n, é→e…) para que el cuerpo
        /// quepa en el alfabeto GSM-7 y el SMS viaje en 1 segmento. Sin esto, una sola tilde fuerza
        /// UCS-2 (70 chars/segmento) y un enlace largo termina partido entre segmentos → no clicable.
        /// </summary>
        private static string NormalizeForGsm7(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                    != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }
    }
}
