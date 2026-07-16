using Microsoft.AspNetCore.Http;

namespace newApi.Services
{
    /// <summary>
    /// 🛡️ [W43-DELIVERABLE-CONTENT] Valida que un entregable de inspección (PDF/MP4) tenga
    /// contenido real, no un fichero basura de 1 byte con la extensión correcta. Antes solo se
    /// comprobaba la extensión y un tope de 10MB (ni magic bytes ni tamaño mínimo), así que un
    /// experto malintencionado podía "entregar" un "informe.pdf" de 1 byte y cobrar el 95% por
    /// auto-aprobación a las 72h si el cliente no reaccionaba a tiempo. Comprueba tamaño mínimo +
    /// firma de fichero (%PDF- para PDF, caja 'ftyp' para MP4). NO valida la calidad del contenido
    /// (eso es humano), solo que sea un fichero del tipo declarado y no un placeholder vacío.
    /// </summary>
    public static class DeliverableContentValidator
    {
        // Un PDF/MP4 legítimo pesa muchos KB; 1 KB descarta 0/1-byte y placeholders sin bloquear
        // ningún entregable real.
        private const long MinBytes = 1024;

        /// <param name="extension">Extensión ya validada y en minúsculas (".pdf" / ".mp4").</param>
        public static (bool Ok, string? Error) Validate(IFormFile file, string extension)
        {
            if (file.Length < MinBytes)
            {
                return (false, $"El archivo {file.FileName} es demasiado pequeño ({file.Length} bytes) para ser un entregable válido. Sube el informe/vídeo real de la inspección.");
            }

            var head = new byte[64];
            int read;
            using (var s = file.OpenReadStream())
            {
                read = s.Read(head, 0, head.Length);
            }
            var span = head.AsSpan(0, read);

            if (extension == ".pdf")
            {
                // "%PDF-" = 25 50 44 46 2D
                if (read < 5 || span[0] != 0x25 || span[1] != 0x50 || span[2] != 0x44 || span[3] != 0x46 || span[4] != 0x2D)
                {
                    return (false, $"El archivo {file.FileName} no es un PDF válido.");
                }
            }
            else if (extension == ".mp4")
            {
                // Caja 'ftyp' (66 74 79 70) presente cerca del inicio (normalmente en el offset 4).
                var hasFtyp = false;
                for (var i = 0; i + 3 < read; i++)
                {
                    if (span[i] == 0x66 && span[i + 1] == 0x74 && span[i + 2] == 0x79 && span[i + 3] == 0x70)
                    {
                        hasFtyp = true;
                        break;
                    }
                }
                if (!hasFtyp)
                {
                    return (false, $"El archivo {file.FileName} no es un MP4 válido.");
                }
            }

            return (true, null);
        }
    }
}
