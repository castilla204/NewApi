using System.Security.Cryptography;

namespace newApi.Common
{
    /// <summary>
    /// Genera tokens de un solo uso para magic-links (confirmación del experto, reserva del
    /// vendedor) usando un CSPRNG.
    ///
    /// FIX [M1] auditoría: estos tokens son la ÚNICA credencial de endpoints [AllowAnonymous]
    /// que CAPTURAN DINERO real (approve → captura del PaymentIntent) y disparan reembolsos/strikes.
    /// Antes se generaban con <c>Guid.NewGuid()</c>, que la propia documentación de .NET desaconseja
    /// para fines de seguridad (no garantiza imprevisibilidad criptográfica). Aquí usamos
    /// <see cref="RandomNumberGenerator"/> (CSPRNG) y codificación base64url (URL-safe, sin padding),
    /// produciendo ~43 chars para 32 bytes → dentro del rango 32-128 que validan los controladores.
    /// </summary>
    public static class SecureTokenHelper
    {
        /// <summary>Genera un token magic-link URL-safe con <paramref name="byteLength"/> bytes de entropía (32 por defecto = 256 bits).</summary>
        public static string GenerateMagicLinkToken(int byteLength = 32)
        {
            var bytes = RandomNumberGenerator.GetBytes(byteLength);
            // base64url: '+' → '-', '/' → '_', se quita el padding '='. URL-safe y comparable como string.
            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }
    }
}
