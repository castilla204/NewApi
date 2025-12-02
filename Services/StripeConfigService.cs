using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    public interface IStripeConfigService
    {
        Task<string> GetStripeModeAsync();
        Task<bool> SetStripeModeAsync(string mode, int? userId);
        Task<(string SecretKey, string WebhookSecret, string GeneralWebhookSecret)> GetStripeKeysForModeAsync(string mode, Func<string, string?, string?> getSecretValue);
    }

    public class StripeConfigService : IStripeConfigService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<StripeConfigService> _logger;

        public StripeConfigService(AppDbContext context, ILogger<StripeConfigService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<string> GetStripeModeAsync()
        {
            try
            {
                var setting = await _context.SystemSettings.FirstOrDefaultAsync();
                return setting?.StripeMode ?? "production";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error obteniendo modo Stripe, usando producción por defecto");
                return "production";
            }
        }

        public async Task<bool> SetStripeModeAsync(string mode, int? userId)
        {
            if (mode != "development" && mode != "production")
            {
                throw new ArgumentException("El modo debe ser 'development' o 'production'");
            }

            try
            {
                var setting = await _context.SystemSettings.FirstOrDefaultAsync();
                if (setting == null)
                {
                    setting = new SystemSetting
                    {
                        StripeMode = mode,
                        StripeModeChangedAt = DateTime.UtcNow,
                        StripeModeChangedByUserId = userId
                    };
                    _context.SystemSettings.Add(setting);
                }
                else
                {
                    setting.StripeMode = mode;
                    setting.StripeModeChangedAt = DateTime.UtcNow;
                    setting.StripeModeChangedByUserId = userId;
                    setting.UpdatedAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();
                _logger.LogInformation($"✅ Modo Stripe cambiado a: {mode} por usuario {userId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cambiando modo Stripe");
                throw;
            }
        }

        public async Task<(string SecretKey, string WebhookSecret, string GeneralWebhookSecret)> GetStripeKeysForModeAsync(
            string mode, 
            Func<string, string?, string?> getSecretValue)
        {
            string secretKeySuffix = mode == "development" ? "-dev" : "";
            string webhookSecretSuffix = mode == "development" ? "-dev" : "";
            string generalWebhookSecretSuffix = mode == "development" ? "-dev" : "";

            // Intentar cargar desde Secret Manager con el sufijo correspondiente
            var secretKey = getSecretValue($"stripe-secret-key{secretKeySuffix}", null) 
                ?? getSecretValue("stripe-secret-key", null) 
                ?? "";
            
            var webhookSecret = getSecretValue($"stripe-webhook-secret{webhookSecretSuffix}", null) 
                ?? getSecretValue("stripe-webhook-secret", null) 
                ?? "";
            
            var generalWebhookSecret = getSecretValue($"stripe-general-webhook-secret{generalWebhookSecretSuffix}", null) 
                ?? getSecretValue("stripe-general-webhook-secret", null) 
                ?? "";

            return (secretKey, webhookSecret, generalWebhookSecret);
        }
    }
}
