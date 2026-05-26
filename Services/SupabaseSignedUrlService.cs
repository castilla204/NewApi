using System;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace newApi.Services
{
    /// <summary>
    /// Implementación de ISignedUrlService sobre Supabase Storage (reemplaza a GoogleSignedUrlService).
    /// Enruta por el PREFIJO del objectName:
    ///   services/ , profiles/ , reviews/   → bucket público 'images' → URL pública permanente.
    ///   disputes/ , deliverables/ , chat/  → bucket privado 'files'  → URL firmada temporal.
    /// Así todas las lecturas existentes (que llaman a GetSignedUrl(objectName)) funcionan sin cambios.
    /// </summary>
    public class SupabaseSignedUrlService : ISignedUrlService
    {
        private readonly ISupabaseStorageService _storage;
        private readonly ILogger<SupabaseSignedUrlService> _logger;

        // Prefijos de contenido PÚBLICO (se sirven con URL pública permanente).
        private static readonly string[] PublicPrefixes = { "services/", "profiles/", "reviews/" };

        public SupabaseSignedUrlService(ISupabaseStorageService storage, ILogger<SupabaseSignedUrlService> logger)
        {
            _storage = storage;
            _logger = logger;
        }

        public string? GetSignedUrl(string objectName, TimeSpan? lifetime = null, HttpMethod? httpMethod = null)
        {
            if (string.IsNullOrWhiteSpace(objectName)) return null;
            var name = objectName.Replace("\\", "/").TrimStart('/');

            // Público → URL pública (sin firmar, permanente).
            foreach (var p in PublicPrefixes)
            {
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return _storage.GetPublicUrl(_storage.ImagesBucket, name);
            }

            // Privado → URL firmada temporal del bucket privado.
            // (Llamada HTTP bloqueante; en ASP.NET Core no hay SynchronizationContext, no hay deadlock.
            //  El contenido privado se lee con poca frecuencia, así que es aceptable.)
            try
            {
                var expiry = lifetime ?? TimeSpan.FromMinutes(30);
                return _storage.CreateSignedUrlAsync(_storage.FilesBucket, name, expiry)
                               .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error firmando URL Supabase para {ObjectName}", objectName);
                return null;
            }
        }
    }
}
