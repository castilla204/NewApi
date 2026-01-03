using System;
using System.IO;
using System.Net.Http;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;

namespace newApi.Services
{
    public class GoogleSignedUrlService : ISignedUrlService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<GoogleSignedUrlService> _logger;
        private readonly IWebHostEnvironment _environment;
        private readonly Lazy<UrlSigner> _lazySigner;

        public GoogleSignedUrlService(IConfiguration configuration, ILogger<GoogleSignedUrlService> logger, IWebHostEnvironment environment)
        {
            _configuration = configuration;
            _logger = logger;
            _environment = environment;
            _lazySigner = new Lazy<UrlSigner>(CreateSigner);
        }

        public string? GetSignedUrl(string objectName, TimeSpan? lifetime = null, HttpMethod? httpMethod = null)
        {
            if (string.IsNullOrWhiteSpace(objectName))
            {
                return null;
            }

            var bucketName = _configuration["GoogleCloud:BucketName"];
            if (string.IsNullOrWhiteSpace(bucketName))
            {
                _logger.LogWarning("Google Cloud bucket name is not configured. Signed URL cannot be generated for {ObjectName}", objectName);
                return null;
            }

            try
            {
                var signer = _lazySigner.Value;
                var expiration = lifetime ?? TimeSpan.FromMinutes(30);
                var method = httpMethod ?? HttpMethod.Get;
                return signer.Sign(bucketName, objectName, expiration, method);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate signed URL for object {ObjectName}", objectName);
                return null;
            }
        }

        private UrlSigner CreateSigner()
        {
            // En producción (Azure App Services): intentar leer de GoogleCredentialJson primero
            if (!_environment.IsDevelopment())
            {
                var credentialsJson = Environment.GetEnvironmentVariable("GoogleCredentialJson");
                if (!string.IsNullOrEmpty(credentialsJson))
                {
                    try
                    {
                        var credential = GoogleCredential.FromJson(credentialsJson);
                        if (credential.UnderlyingCredential is ServiceAccountCredential serviceAccountCredential)
                        {
                            _logger.LogInformation("✅ Usando credenciales desde GoogleCredentialJson (Azure App Services)");
                            return UrlSigner.FromServiceAccountCredential(serviceAccountCredential);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error al crear UrlSigner desde GoogleCredentialJson, intentando fallback...");
                    }
                }
            }

            // First attempt: use explicit credentials path if provided
            var credentialsPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
            if (!string.IsNullOrEmpty(credentialsPath) && File.Exists(credentialsPath))
            {
                return UrlSigner.FromServiceAccountPath(credentialsPath);
            }

            // Second attempt: use application default credentials and ensure they are service account creds
            var defaultCredential = GoogleCredential.GetApplicationDefault();
            if (defaultCredential.UnderlyingCredential is ServiceAccountCredential defaultServiceAccountCredential)
            {
                return UrlSigner.FromServiceAccountCredential(defaultServiceAccountCredential);
            }

            throw new InvalidOperationException("Unable to create UrlSigner. Service account credentials are required to generate signed URLs.");
        }
    }
}
