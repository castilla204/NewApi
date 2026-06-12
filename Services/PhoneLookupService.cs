using Microsoft.Extensions.Configuration;
using Twilio;
using Twilio.Rest.Lookups.V2;

namespace newApi.Services
{
    /// <summary>
    /// 📱 Clasifica un teléfono como mobile/landline/voip/unknown con Twilio Lookup v2
    /// (line_type_intelligence). Un FIJO no recibe SMS, así que el panel del experto lo
    /// marca como no válido y pide cargar un móvil verificado por OTP.
    ///
    /// Gated por credenciales (mismas que SmsService). Fallback sin Twilio: heurística
    /// para España (+34: 6/7 = móvil, 8/9 = fijo). Best-effort: nunca lanza.
    /// </summary>
    public interface IPhoneLookupService
    {
        /// <summary>Devuelve "mobile" | "landline" | "voip" | "unknown".</summary>
        Task<string> GetLineTypeAsync(string phoneNumber);
    }

    public class PhoneLookupService : IPhoneLookupService
    {
        private readonly string _accountSid;
        private readonly string _authToken;

        public PhoneLookupService(IConfiguration configuration)
        {
            _accountSid = configuration["Twilio:AccountSid"] ?? "";
            _authToken = configuration["Twilio:AuthToken"] ?? "";
        }

        public async Task<string> GetLineTypeAsync(string phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber)) return "unknown";
            var normalized = phoneNumber.Trim();

            if (!string.IsNullOrWhiteSpace(_accountSid) && !string.IsNullOrWhiteSpace(_authToken))
            {
                try
                {
                    TwilioClient.Init(_accountSid, _authToken);
                    var result = await PhoneNumberResource.FetchAsync(
                        pathPhoneNumber: normalized,
                        fields: "line_type_intelligence");

                    // line_type_intelligence.type: mobile | landline | fixedVoip | nonFixedVoip | ...
                    var type = (result?.LineTypeIntelligence?.Type ?? "").ToLowerInvariant();
                    if (type.Contains("mobile")) return "mobile";
                    if (type.Contains("landline") || type.Contains("fixed") && !type.Contains("voip")) return "landline";
                    if (type.Contains("voip")) return "voip";
                    if (!string.IsNullOrEmpty(type)) return "unknown";
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PHONE-LOOKUP] Twilio Lookup falló (uso heurística): {ex.Message}");
                }
            }

            return HeuristicLineType(normalized);
        }

        /// <summary>Heurística sin Twilio: España (+34) móvil empieza por 6/7, fijo por 8/9.</summary>
        internal static string HeuristicLineType(string phone)
        {
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            string? national = null;
            if (phone.StartsWith("+34")) national = digits.Length > 2 ? digits[2..] : null;
            else if (digits.StartsWith("34") && digits.Length == 11) national = digits[2..];
            else if (digits.Length == 9) national = digits; // nacional ES sin prefijo

            if (national is { Length: 9 })
            {
                return national[0] switch
                {
                    '6' or '7' => "mobile",
                    '8' or '9' => "landline",
                    _ => "unknown",
                };
            }
            return "unknown";
        }
    }
}
