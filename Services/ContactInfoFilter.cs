using System.Text.RegularExpressions;

namespace newApi.Services
{
    public enum ContactType
    {
        Phone,
        Email,
        Url,
        SocialOrApp
    }

    public sealed record ContactDetectionResult(bool HasViolation, IReadOnlyList<ContactType> Types);

    /// <summary>
    /// Detecta intentos de compartir datos de contacto o llevar la conversación
    /// fuera de la plataforma. Pensado para mensajes de PRECONTRATACIÓN.
    /// Umbral de teléfono = 9 dígitos para no marcar precios, fechas (8 díg.) ni modelos.
    /// </summary>
    public static class ContactInfoFilter
    {
        // Email estándar.
        private static readonly Regex EmailRegex = new(
            @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Email ofuscado: "x arroba y punto com", "x (at) y".
        private static readonly Regex ObfuscatedEmailRegex = new(
            @"\b[\w.]+\s*(?:arroba|\(at\)|\[at\])\s*[\w.]+\s*(?:punto|\.)\s*\w{2,}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // URL: http(s), www. o dominio.tld con TLD conocido (evita foto.jpg, archivo.pdf).
        private static readonly Regex UrlRegex = new(
            @"\b(?:https?://|www\.)\S{1,512}|\b[a-z0-9\-]+\.(?:com|net|org|es|io|app|me|info|biz|co|gg|tv|online|site|web)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Redes / apps / intentos de salir de la plataforma.
        private static readonly Regex SocialRegex = new(
            @"whats\s?app|whatsap|wasap|wssp|wsp|telegram|t\.me|instagram|\binsta\b|tiktok|\bsignal\b|facebook|ll[áa]mame|mi\s+n[úu]mero|te\s+paso\s+el|fuera\s+de\s+la\s+(?:app|plataforma)|@[A-Za-z0-9_.]{3,}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Secuencia de dígitos con separadores que contiene >= 9 dígitos.
        // CHAT-FILTER FIX: antes solo admitía espacio y guion como separador, así que un teléfono
        // escrito con PUNTOS/BARRAS/punto-medio (formato europeo habitual: "600.123.456", "600/123/456",
        // "666·777·888") evadía el filtro → fuga de comisión. Añadidos . / · (el no-break space ya lo
        // cubre \s en .NET). El umbral de 9 dígitos sigue descartando fechas (8 díg.) y precios.
        private static readonly Regex DigitRunRegex = new(
            @"\d[\d\s\.\-\/·]{7,}\d",
            RegexOptions.Compiled);

        // >= 7 números deletreados en español seguidos.
        private static readonly Regex SpelledNumbersRegex = new(
            @"\b(?:cero|uno|dos|tres|cuatro|cinco|seis|siete|ocho|nueve)\b(?:\s+\b(?:cero|uno|dos|tres|cuatro|cinco|seis|siete|ocho|nueve)\b){6,}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static ContactDetectionResult Detect(string? content)
        {
            var types = new List<ContactType>();

            if (string.IsNullOrWhiteSpace(content))
            {
                return new ContactDetectionResult(false, types);
            }

            if (EmailRegex.IsMatch(content) || ObfuscatedEmailRegex.IsMatch(content))
            {
                types.Add(ContactType.Email);
            }

            if (UrlRegex.IsMatch(content))
            {
                types.Add(ContactType.Url);
            }

            if (SocialRegex.IsMatch(content))
            {
                types.Add(ContactType.SocialOrApp);
            }

            if (HasPhone(content))
            {
                types.Add(ContactType.Phone);
            }

            return new ContactDetectionResult(types.Count > 0, types);
        }

        private static bool HasPhone(string content)
        {
            if (SpelledNumbersRegex.IsMatch(content))
            {
                return true;
            }

            foreach (Match m in DigitRunRegex.Matches(content))
            {
                var digits = 0;
                foreach (var c in m.Value)
                {
                    if (char.IsDigit(c)) digits++;
                }
                if (digits >= 9)
                {
                    return true;
                }
            }

            return false;
        }

        public static string BuildBlockMessage(IReadOnlyList<ContactType> types)
        {
            return "Por tu seguridad no puedes compartir teléfonos, correos, enlaces ni redes " +
                   "sociales antes de contratar. Cuando contrates el servicio podréis intercambiar " +
                   "esos datos para coordinaros.";
        }
    }
}
