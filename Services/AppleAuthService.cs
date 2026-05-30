using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace newApi.Services
{
    /// <summary>
    /// 🛡️ Round 16: validación de identityToken de "Sign in with Apple".
    ///
    /// Flujo cliente → backend:
    ///  1. Usuario pulsa "Sign in with Apple" en web/iOS/macOS.
    ///  2. Apple devuelve un JWT identityToken firmado RS256 con sus claves públicas.
    ///  3. Backend descarga JWKS de https://appleid.apple.com/auth/keys (cacheado 24h).
    ///  4. Backend valida firma + iss + aud + exp + nonce (si se envió).
    ///  5. Extrae sub (Apple user id, estable) + email (puede ser proxy @privaterelay.appleid.com).
    ///  6. Match/Create User vía sub (campo AppleId en BD).
    ///
    /// Notas críticas:
    ///  - "sub" es el identificador estable; el email puede cambiar (privacidad Apple).
    ///  - El email solo viene en el primer login. En sucesivos, sub es la única clave.
    ///  - "aud" debe ser el Service ID (web) o Bundle ID (iOS) configurado en Apple Developer.
    ///  - Nonce opcional pero recomendado (anti-replay). Cliente lo genera y envía hash en
    ///    "nonce" del auth request; Apple lo devuelve en el token.
    /// </summary>
    public interface IAppleAuthService
    {
        /// <summary>
        /// Valida el identityToken y devuelve los claims relevantes si todo es correcto.
        /// Devuelve null si la validación falla por cualquier motivo (firma, expiración, audiencia).
        /// </summary>
        Task<AppleIdentityClaims?> ValidateIdentityTokenAsync(
            string identityToken,
            string? expectedNonce = null,
            CancellationToken ct = default);
    }

    /// <summary>Claims extraídos del identityToken de Apple tras validación.</summary>
    public record AppleIdentityClaims(
        string Sub,            // Apple user identifier (estable per app+team).
        string? Email,         // Puede ser proxy (@privaterelay.appleid.com) o real.
        bool EmailVerified,    // Apple SIEMPRE verifica emails (true en práctica).
        bool IsPrivateEmail);  // True si Apple oculta el email real del usuario.

    /// <inheritdoc />
    public class AppleAuthService : IAppleAuthService
    {
        private const string AppleIssuer = "https://appleid.apple.com";
        private const string JwksPath = "auth/keys";
        private const string JwksCacheKey = "apple:jwks";
        private static readonly TimeSpan JwksCacheTtl = TimeSpan.FromHours(24);

        private readonly IHttpClientFactory _httpFactory;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _config;
        private readonly ILogger<AppleAuthService> _logger;

        public AppleAuthService(
            IHttpClientFactory httpFactory,
            IMemoryCache cache,
            IConfiguration config,
            ILogger<AppleAuthService> logger)
        {
            _httpFactory = httpFactory;
            _cache = cache;
            _config = config;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<AppleIdentityClaims?> ValidateIdentityTokenAsync(
            string identityToken,
            string? expectedNonce = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(identityToken))
            {
                _logger.LogWarning("Apple validate: identityToken vacío");
                return null;
            }

            // ─── Audiencias aceptadas (cliente web Service ID + iOS Bundle ID) ─────────
            // Se configuran en appsettings (Apple:Audiences = ["com.inspecciono.signin", "com.inspecciono.app"]).
            var audiences = _config.GetSection("Apple:Audiences").Get<string[]>()
                ?? new[] { "com.inspecciono.signin", "com.inspecciono.app" };

            // ─── Cargar JWKS (cacheado 24h) ────────────────────────────────────────────
            JsonWebKeySet? jwks;
            try
            {
                jwks = await GetJwksAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Apple validate: fallo al cargar JWKS");
                return null;
            }

            if (jwks == null || jwks.Keys.Count == 0)
            {
                _logger.LogError("Apple validate: JWKS vacío");
                return null;
            }

            // ─── Validación del JWT ────────────────────────────────────────────────────
            var handler = new JsonWebTokenHandler { MapInboundClaims = false };
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = AppleIssuer,
                ValidateAudience = true,
                ValidAudiences = audiences,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2),
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = jwks.Keys,
                ValidAlgorithms = new[] { "RS256" }, // Apple solo firma con RS256
            };

            var result = await handler.ValidateTokenAsync(identityToken, validationParameters);

            if (!result.IsValid)
            {
                _logger.LogWarning(result.Exception, "Apple validate: token inválido");
                return null;
            }

            // ─── Verificar nonce (anti-replay) ─────────────────────────────────────────
            // Apple devuelve el nonce SIN hashear (es el valor literal que envió el cliente).
            // El cliente puede haber enviado hash(nonce) — el contrato es del lado del cliente.
            if (!string.IsNullOrEmpty(expectedNonce))
            {
                if (!result.Claims.TryGetValue("nonce", out var nonceObj) ||
                    !string.Equals(nonceObj?.ToString(), expectedNonce, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Apple validate: nonce mismatch");
                    return null;
                }
            }

            // ─── Extraer claims ────────────────────────────────────────────────────────
            var sub = result.Claims.TryGetValue("sub", out var subObj) ? subObj?.ToString() : null;
            var email = result.Claims.TryGetValue("email", out var emailObj) ? emailObj?.ToString() : null;
            var emailVerified = result.Claims.TryGetValue("email_verified", out var evObj)
                && (evObj?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
                    || evObj is bool b && b);
            var isPrivateEmail = result.Claims.TryGetValue("is_private_email", out var peObj)
                && (peObj?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
                    || peObj is bool pe && pe);

            if (string.IsNullOrEmpty(sub))
            {
                _logger.LogError("Apple validate: token sin 'sub' tras validación");
                return null;
            }

            return new AppleIdentityClaims(sub!, email, emailVerified, isPrivateEmail);
        }

        /// <summary>
        /// Descarga el JWKS de Apple (https://appleid.apple.com/auth/keys) o devuelve el cacheado.
        /// Cache 24h para limitar rate calls. Apple rota claves cada ~6 meses.
        /// </summary>
        private async Task<JsonWebKeySet?> GetJwksAsync(CancellationToken ct)
        {
            if (_cache.TryGetValue<JsonWebKeySet>(JwksCacheKey, out var cached) && cached != null)
            {
                return cached;
            }

            var client = _httpFactory.CreateClient("apple-jwks");
            using var response = await client.GetAsync(JwksPath, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var jwks = new JsonWebKeySet(json);

            _cache.Set(JwksCacheKey, jwks, JwksCacheTtl);
            _logger.LogInformation("Apple JWKS cargado y cacheado ({Count} claves).", jwks.Keys.Count);
            return jwks;
        }
    }
}
