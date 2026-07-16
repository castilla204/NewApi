using System.Text;
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

        // Email ofuscado con tokens SIMBÓLICOS de "at" (arroba/(at)/[at]) — NO aparecen en prosa, así
        // que admite CUALQUIER forma de "dot" (punto, ".", la palabra "dot", (dot), [dot]) sin riesgo
        // de falso positivo. Casa "x arroba y punto com", "x (at) y dot com", "x [at] y . com".
        private static readonly Regex ObfuscatedEmailSymbolRegex = new(
            @"\b[\w.]+\s*(?:arroba|\(at\)|\[at\])\s*[\w.]+\s*(?:punto|dot|\.|\(dot\)|\[dot\])\s*\w{2,}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // W26(b) FIX v2: ofuscación DELETREADA en inglés ("juan at gmail dot com", "juan at gmail punto
        // com"). La palabra suelta "at" es ambigua con la prosa, así que aquí se blinda por 3 lados para
        // NO marcar prosa: (1) "at" nunca se empareja con un "." literal (solo la palabra dot/punto o
        // (dot)/[dot]) — descarta "the car is at 20000. It runs great"; (2) el segmento final debe ser un
        // TLD conocido — descarta "look at that. Amazing"; (3) la parte de dominio NO puede ser un
        // artículo/determinante común (the/that/my/…) vía lookahead negativo — descarta el falso positivo
        // "look at the dot com listing" SIN perder "juan at gmail dot com" (un dominio real nunca es
        // "the"/"that"). Corrige tanto el FP residual como los huecos cross-style del intento v1.
        private static readonly Regex ObfuscatedEmailSpelledRegex = new(
            @"\b[\w.]+\s+at\s+(?!(?:the|a|an|that|this|these|those|my|your|our|his|her|their|its|some|any|no|one)\s)[\w.]+\s+(?:dot|punto|\(dot\)|\[dot\])\s+(?:com|net|org|es|io|app|me|info|biz|co|gg|tv|online|site|web)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "@" LITERAL suelto y espaciado ("juan @ gmail dot com", "juan @ gmail . com"). El símbolo "@" SÍ
        // aparece en prosa (como "at" o en horas), a diferencia de "arroba"/"(at)", así que aquí NO basta el
        // "@": se blinda EXIGIENDO un TLD conocido al final (misma técnica que ObfuscatedEmailSpelledRegex)
        // para no marcar "quedamos @ el punto de encuentro" (acaba en "encuentro", no en TLD). Casa el email
        // espaciado real; descarta la prosa con "@" + "punto".
        private static readonly Regex ObfuscatedEmailBareAtRegex = new(
            @"\b[\w.]+\s*@\s*[\w.]+\s*(?:punto|dot|\.|\(dot\)|\[dot\])\s*(?:com|net|org|es|io|app|me|info|biz|co|gg|tv|online|site|web)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // URL explícita: http(s):// o www. — bajísimo FP, siempre se comprueba.
        private static readonly Regex UrlExplicitRegex = new(
            @"\b(?:https?://|www\.)\S{1,512}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Dominio "pelado" dominio.tld sin http/www. Cubre la mayoría de TLDs conocidos (evita foto.jpg).
        private static readonly Regex UrlBareDomainRegex = new(
            @"\b[a-z0-9\-]+\.(?:com|net|org|es|io|app|me|info|biz|co|gg|tv|online|site|web)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Variante "prose-safe" del dominio pelado: EXCLUYE los TLDs cortos que colisionan con palabras
        // españolas frecuentes — .es (verbo "es"), .me (pronombre "me"), .co ("co-"). En texto largo en
        // español ("revisamos el coche.Es fundamental…", "compra segura.es…") esos TLDs disparaban un FALSO
        // POSITIVO masivo (verificado ~40% de descripciones legítimas). Se usa al filtrar CAMPOS PÚBLICOS de
        // texto libre (descripción de servicio/bio/reseña), donde un FP bloquearía un listado legítimo; el
        // chat (mensajes cortos) mantiene la variante completa. Los .com/.net/.org… siguen cazándose.
        private static readonly Regex UrlBareDomainProseSafeRegex = new(
            @"\b[a-z0-9\-]+\.(?:com|net|org|io|app|info|biz|gg|tv|online|site|web)\b",
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

        // W26 FIX: caracteres invisibles / de formato (ancho cero, BOM, soft-hyphen, marcas
        // direccionales, word-joiner). NO pertenecen a la clase \s de .NET (\s = categoría Z), así que
        // insertando un ZWSP entre cada dígito ("6[ZWSP]0[ZWSP]0…") la "digit run" nunca alcanzaba los
        // 9 dígitos contiguos y el teléfono evadía el filtro (fuga de comisión). Se comparan por código
        // (hex) para no meter caracteres invisibles en el fuente. Se eliminan de una COPIA usada solo
        // para la detección — el contenido almacenado NO se toca, así que las secuencias ZWJ de emoji
        // (p. ej. familias 👨‍👩‍👧) siguen intactas en el mensaje guardado.
        private static bool IsInvisibleFormatChar(char c)
        {
            int u = c;
            return u == 0x00AD                      // soft hyphen
                || u == 0x061C                      // arabic letter mark
                || u == 0x180E                      // mongolian vowel separator
                || (u >= 0x200B && u <= 0x200F)     // ZWSP, ZWNJ, ZWJ, LRM, RLM
                || (u >= 0x2060 && u <= 0x2064)     // word joiner + invisible operators
                || (u >= 0x2066 && u <= 0x206F)     // directional isolates / formatting
                || u == 0xFEFF;                     // BOM / ZWNBSP
        }

        private static string StripInvisibleChars(string input)
        {
            // Recorre una vez; solo asigna un StringBuilder si de verdad hay algo que quitar.
            var idx = -1;
            for (var i = 0; i < input.Length; i++)
            {
                if (IsInvisibleFormatChar(input[i])) { idx = i; break; }
            }
            if (idx < 0) return input;

            var sb = new StringBuilder(input.Length);
            sb.Append(input, 0, idx);
            for (var i = idx; i < input.Length; i++)
            {
                if (!IsInvisibleFormatChar(input[i])) sb.Append(input[i]);
            }
            return sb.ToString();
        }

        /// <param name="proseSafeUrls">
        /// true al filtrar CAMPOS PÚBLICOS de texto largo en español (descripción de servicio/bio/reseña):
        /// usa la variante de dominio pelado que excluye .es/.me/.co para no falsar-positivar prosa. El chat
        /// llama sin este flag (variante completa; mensajes cortos, FP tolerado).
        /// </param>
        public static ContactDetectionResult Detect(string? content, bool proseSafeUrls = false)
        {
            var types = new List<ContactType>();

            if (string.IsNullOrWhiteSpace(content))
            {
                return new ContactDetectionResult(false, types);
            }

            // W26 FIX: normalizar quitando invisibles ANTES de correr las regex; si no, un ZWSP entre
            // dígitos/letras rompe las "digit runs" y la ofuscación de email/URL. Copia local, no muta
            // nada persistido.
            content = StripInvisibleChars(content);

            if (EmailRegex.IsMatch(content) || ObfuscatedEmailSymbolRegex.IsMatch(content) || ObfuscatedEmailSpelledRegex.IsMatch(content) || ObfuscatedEmailBareAtRegex.IsMatch(content))
            {
                types.Add(ContactType.Email);
            }

            var bareDomainRegex = proseSafeUrls ? UrlBareDomainProseSafeRegex : UrlBareDomainRegex;
            if (UrlExplicitRegex.IsMatch(content) || bareDomainRegex.IsMatch(content))
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
