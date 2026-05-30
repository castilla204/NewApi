using System;
using BCrypt.Net;

namespace newApi.Services
{
    /// <summary>
    /// 🛡️ Round 16: hashing y verificación de contraseñas con BCrypt.
    ///
    /// Decisiones de seguridad (NIST SP 800-63B-4 §5.1.1.2 + OWASP 2025):
    ///  - Algoritmo: BCrypt (work factor 12 → ~250ms en hardware moderno).
    ///  - Salt: integrado, 16 bytes random por hash (BCrypt lo gestiona internamente).
    ///  - Output: string de 60 chars con cost+salt+hash empaquetado — guardable en columna text.
    ///  - HIBP check OPCIONAL en VerifyPwnedAsync (network call → no en el path crítico).
    ///
    /// ¿Por qué BCrypt y no Argon2id?
    ///  - Argon2id es preferido por OWASP, pero la lib disponible (Konscious.Security.Cryptography.Argon2)
    ///    es menos auditada que BCrypt.Net-Next (5M+ downloads/mes).
    ///  - BCrypt sigue siendo aceptado por NIST y OWASP cuando work factor ≥ 12.
    ///  - El proyecto ya tiene BCrypt.Net-Next 4.0.3 instalada (csproj:18).
    ///
    /// Política de complejidad mínima (en ValidatePolicy):
    ///  - ≥ 8 chars (NIST SHALL ≥ 8).
    ///  - ≤ 128 chars (NIST SHOULD ≤ 64 mínimo, ampliamos por UX moderna).
    ///  - NO exigir mayúsc/minúsc/dígito/símbolo (NIST §5.1.1.2: "verifiers SHALL NOT impose
    ///    composition rules"). Sí exigimos blocklist HIBP en endpoint para evitar passwords comunes.
    /// </summary>
    public interface IPasswordHashingService
    {
        /// <summary>Hashea la contraseña con BCrypt (work factor 12). Devuelve string serializado de 60 chars.</summary>
        string Hash(string password);

        /// <summary>Verifica que la contraseña coincida con el hash. Constant-time gracias a BCrypt.</summary>
        bool Verify(string password, string hash);

        /// <summary>True si la política mínima de complejidad pasa; reason explica el fallo.</summary>
        bool ValidatePolicy(string password, out string reason);

        /// <summary>
        /// Consulta HIBP Pwned Passwords API con k-anonimato (envía 5 primeros chars del SHA-1).
        /// Devuelve la cantidad de breaches en que apareció, o 0 si no aparece. Network call.
        /// </summary>
        Task<int> CheckHibpAsync(string password, CancellationToken ct = default);
    }

    /// <inheritdoc />
    public class PasswordHashingService : IPasswordHashingService
    {
        // Work factor BCrypt. 12 ≈ 250ms en hardware moderno (NIST recomienda apuntar a 250ms-1s).
        // Cambiar a 13 en 2027-2028 cuando hardware mejore.
        private const int WorkFactor = 12;
        private const int MinLength = 8;   // NIST SP 800-63B-4 §5.1.1.2 SHALL
        private const int MaxLength = 128; // amplio para passphrases largas

        private readonly HttpClient _http;
        private readonly Microsoft.Extensions.Logging.ILogger<PasswordHashingService> _logger;

        public PasswordHashingService(IHttpClientFactory httpFactory, Microsoft.Extensions.Logging.ILogger<PasswordHashingService> logger)
        {
            _http = httpFactory.CreateClient("hibp");
            _logger = logger;
        }

        /// <inheritdoc />
        public string Hash(string password)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password cannot be empty.", nameof(password));
            return BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
        }

        /// <inheritdoc />
        public bool Verify(string password, string hash)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash))
                return false;
            try
            {
                return BCrypt.Net.BCrypt.Verify(password, hash);
            }
            catch (SaltParseException)
            {
                // Hash corrupto / no es BCrypt. No revelar detalle al caller.
                return false;
            }
        }

        /// <inheritdoc />
        public bool ValidatePolicy(string password, out string reason)
        {
            if (string.IsNullOrEmpty(password))
            {
                reason = "La contraseña es obligatoria.";
                return false;
            }
            if (password.Length < MinLength)
            {
                reason = $"La contraseña debe tener al menos {MinLength} caracteres.";
                return false;
            }
            if (password.Length > MaxLength)
            {
                reason = $"La contraseña no puede superar los {MaxLength} caracteres.";
                return false;
            }
            // Detectar contraseñas triviales como "12345678" o "aaaaaaaa" (sin entropía).
            // Heurística: si todos los chars son iguales o solo dígitos consecutivos, rechazar.
            if (IsAllSame(password) || IsTrivialSequence(password))
            {
                reason = "Esta contraseña es demasiado predecible. Usa una combinación menos común.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        /// <inheritdoc />
        public async Task<int> CheckHibpAsync(string password, CancellationToken ct = default)
        {
            // k-anonimato: enviamos solo los primeros 5 chars del SHA-1 hex. HIBP devuelve
            // todos los hashes que empiezan así (~500 sufijos). Buscamos el resto del hash localmente.
            // Esto evita enviar el password (ni siquiera hasheado completo) a un tercero.
            try
            {
                using var sha1 = System.Security.Cryptography.SHA1.Create();
                var bytes = System.Text.Encoding.UTF8.GetBytes(password);
                var hash = Convert.ToHexString(sha1.ComputeHash(bytes)).ToUpperInvariant();
                var prefix = hash.Substring(0, 5);
                var suffix = hash.Substring(5);

                var response = await _http.GetAsync($"https://api.pwnedpasswords.com/range/{prefix}", ct);
                if (!response.IsSuccessStatusCode)
                {
                    // Fallar OPEN: si HIBP no responde, no bloqueamos al usuario.
                    _logger.LogWarning("HIBP no disponible (status={Status}). Saltando blocklist check.", response.StatusCode);
                    return 0;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                foreach (var line in body.Split('\n'))
                {
                    var trim = line.Trim();
                    if (trim.Length == 0) continue;
                    var parts = trim.Split(':');
                    if (parts.Length != 2) continue;
                    if (string.Equals(parts[0], suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        return int.TryParse(parts[1], out var count) ? count : 1;
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                // Fallar OPEN ante cualquier excepción (red, parsing) — no degradar UX por dependencia externa.
                _logger.LogWarning(ex, "HIBP check falló — saltando blocklist.");
                return 0;
            }
        }

        private static bool IsAllSame(string s)
        {
            if (s.Length < 2) return false;
            char first = s[0];
            for (int i = 1; i < s.Length; i++) if (s[i] != first) return false;
            return true;
        }

        private static bool IsTrivialSequence(string s)
        {
            // "12345678", "abcdefgh", "87654321", etc. — secuencias ascendentes/descendentes.
            if (s.Length < 4) return false;
            bool asc = true, desc = true;
            for (int i = 1; i < s.Length; i++)
            {
                if (s[i] != s[i - 1] + 1) asc = false;
                if (s[i] != s[i - 1] - 1) desc = false;
                if (!asc && !desc) return false;
            }
            return asc || desc;
        }
    }
}
