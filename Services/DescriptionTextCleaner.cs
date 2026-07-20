using System.Text.RegularExpressions;

namespace newApi.Services
{
    /// <summary>
    /// Red de seguridad post-IA: elimina emojis, iconos y markdown que el modelo
    /// pudiera devolver pese a las instrucciones, y recorta al máximo de caracteres.
    /// Garantiza que nunca llegue un "icono raro" a la UI.
    /// </summary>
    public static class DescriptionTextCleaner
    {
        // Símbolos pictográficos (\p{So}), surrogates de emojis astrales (\p{Cs}),
        // selector de variación (U+FE0F) y ZWJ (U+200D).
        private static readonly Regex EmojiRegex = new(
            @"[\p{Cs}\p{So}\uFE0F\u200D]",
            RegexOptions.Compiled);

        private static readonly Regex HeadingRegex = new(
            @"(?m)^\s{0,3}#{1,6}\s*",
            RegexOptions.Compiled);

        private static readonly Regex BulletRegex = new(
            @"(?m)^\s*[-*•◦▪‣·]\s+",
            RegexOptions.Compiled);

        private static readonly Regex MultiSpaceRegex = new(@"[ \t]{2,}", RegexOptions.Compiled);
        private static readonly Regex MultiNewlineRegex = new(@"\n{3,}", RegexOptions.Compiled);

        public static string Clean(string? text, int maxLength)
        {
            var t = Normalize(text);
            if (string.IsNullOrEmpty(t))
            {
                return string.Empty;
            }

            if (t.Length > maxLength)
            {
                t = t.Substring(0, maxLength);
                var lastSpace = t.LastIndexOf(' ');
                if (lastSpace > maxLength / 2)
                {
                    t = t.Substring(0, lastSpace);
                }
                t = t.TrimEnd();
            }

            return t;
        }

        /// <summary>
        /// Aplica la misma normalización que Clean (sin truncar) y dice si el resultado
        /// seguiría superando maxLength. Permite decidir si conviene reintentar la
        /// generación antes de recurrir al recorte por palabra como red de seguridad.
        /// </summary>
        public static bool ExceedsLength(string? text, int maxLength)
        {
            return Normalize(text).Length > maxLength;
        }

        private static string Normalize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var t = text;
            t = EmojiRegex.Replace(t, string.Empty);
            t = t.Replace("**", string.Empty).Replace("__", string.Empty);
            t = HeadingRegex.Replace(t, string.Empty);
            t = BulletRegex.Replace(t, string.Empty);
            t = MultiSpaceRegex.Replace(t, " ");
            t = MultiNewlineRegex.Replace(t, "\n\n");
            return t.Trim();
        }
    }
}
