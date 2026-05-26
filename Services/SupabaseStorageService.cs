using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace newApi.Services
{
    /// <summary>
    /// Implementación de almacenamiento sobre la REST API de Supabase Storage.
    /// Config (appsettings o env var con doble guion bajo en Render):
    ///   SupabaseStorage:Url, SupabaseStorage:ServiceRoleKey, SupabaseStorage:ImagesBucket, SupabaseStorage:FilesBucket
    /// </summary>
    public class SupabaseStorageService : ISupabaseStorageService
    {
        private readonly HttpClient _http;
        private readonly ILogger<SupabaseStorageService> _logger;
        private readonly string _baseUrl;
        private readonly string _serviceKey;

        public string ImagesBucket { get; }
        public string FilesBucket { get; }
        public bool IsConfigured => !string.IsNullOrEmpty(_baseUrl) && !string.IsNullOrEmpty(_serviceKey);

        public SupabaseStorageService(HttpClient http, IConfiguration config, ILogger<SupabaseStorageService> logger)
        {
            _http = http;
            _logger = logger;
            _baseUrl = (config["SupabaseStorage:Url"] ?? string.Empty).TrimEnd('/');
            _serviceKey = config["SupabaseStorage:ServiceRoleKey"] ?? string.Empty;
            ImagesBucket = config["SupabaseStorage:ImagesBucket"] ?? "images";
            FilesBucket = config["SupabaseStorage:FilesBucket"] ?? "files";
        }

        public async Task<string> UploadAsync(string bucket, string objectPath, Stream content, string contentType, CancellationToken ct = default)
        {
            if (!IsConfigured)
                throw new InvalidOperationException("Supabase Storage no configurado (SupabaseStorage:Url / ServiceRoleKey).");

            objectPath = NormalizePath(objectPath);

            // Copiar a memoria para fijar Content-Length (Supabase lo exige).
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            ms.Position = 0;

            var url = $"{_baseUrl}/storage/v1/object/{bucket}/{objectPath}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceKey);
            req.Headers.TryAddWithoutValidation("apikey", _serviceKey);
            req.Headers.TryAddWithoutValidation("x-upsert", "true"); // permitir sobrescritura

            var body = new StreamContent(ms);
            body.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
            body.Headers.ContentLength = ms.Length;
            req.Content = body;

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogError("Supabase upload falló ({Status}) {Bucket}/{Path}: {Body}",
                    (int)resp.StatusCode, bucket, objectPath, err);
                throw new HttpRequestException($"Supabase upload failed ({(int)resp.StatusCode}): {err}");
            }
            return objectPath;
        }

        public string GetPublicUrl(string bucket, string objectPath)
        {
            if (string.IsNullOrWhiteSpace(objectPath)) return string.Empty;
            return $"{_baseUrl}/storage/v1/object/public/{bucket}/{NormalizePath(objectPath)}";
        }

        public async Task<string?> CreateSignedUrlAsync(string bucket, string objectPath, TimeSpan expiry, CancellationToken ct = default)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(objectPath)) return null;
            objectPath = NormalizePath(objectPath);
            try
            {
                var url = $"{_baseUrl}/storage/v1/object/sign/{bucket}/{objectPath}";
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceKey);
                req.Headers.TryAddWithoutValidation("apikey", _serviceKey);
                req.Content = new StringContent(
                    JsonSerializer.Serialize(new { expiresIn = (int)expiry.TotalSeconds }),
                    Encoding.UTF8, "application/json");

                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Supabase signed URL falló ({Status}) {Bucket}/{Path}: {Body}",
                        (int)resp.StatusCode, bucket, objectPath, err);
                    return null;
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("signedURL", out var s))
                {
                    var rel = s.GetString();
                    if (!string.IsNullOrEmpty(rel))
                        return $"{_baseUrl}/storage/v1{(rel.StartsWith("/") ? rel : "/" + rel)}";
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creando signed URL Supabase {Bucket}/{Path}", bucket, objectPath);
                return null;
            }
        }

        public async Task<bool> DeleteAsync(string bucket, string objectPath, CancellationToken ct = default)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(objectPath)) return false;
            objectPath = NormalizePath(objectPath);
            try
            {
                var url = $"{_baseUrl}/storage/v1/object/{bucket}/{objectPath}";
                using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceKey);
                req.Headers.TryAddWithoutValidation("apikey", _serviceKey);
                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Supabase delete falló ({Status}) {Bucket}/{Path}: {Body}",
                        (int)resp.StatusCode, bucket, objectPath, err);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error borrando objeto Supabase {Bucket}/{Path}", bucket, objectPath);
                return false;
            }
        }

        private static string NormalizePath(string p) => (p ?? string.Empty).Replace("\\", "/").TrimStart('/');
    }
}
