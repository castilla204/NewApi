using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.Services;
using Stripe;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Grpc.Core;

namespace newApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "Admin")]
    [EnableRateLimiting("admin")] // ✅ SEGURIDAD: 200 requests/minuto para admin
    public class AdminController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly StripeRefundService _refundService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AdminController> _logger;
        private static string? _currentStripeMode = null; // "production" o "test"
        
        public AdminController(AppDbContext context, StripeRefundService refundService, IConfiguration configuration, ILogger<AdminController> logger)
        {
            _context = context;
            _refundService = refundService;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Obtiene el modo actual de Stripe (production o test)
        /// </summary>
        [HttpGet("stripe/mode")]
        public IActionResult GetStripeMode()
        {
            var currentMode = _currentStripeMode ?? "production";
            var currentKey = StripeConfiguration.ApiKey;
            var isTestMode = currentKey?.StartsWith("sk_test_") == true;
            
            return Ok(new
            {
                mode = isTestMode ? "test" : "production",
                keyPrefix = currentKey?.Substring(0, Math.Min(10, currentKey?.Length ?? 0)) + "...",
                keyLength = currentKey?.Length ?? 0
            });
        }

        /// <summary>
        /// Cambia el modo de Stripe entre producción y prueba
        /// </summary>
        [HttpPost("stripe/toggle-mode")]
        public IActionResult ToggleStripeMode([FromBody] ToggleStripeModeRequest? request = null)
        {
            try
            {
                // Determinar el modo objetivo
                var targetMode = request?.mode?.ToLower() ?? "toggle";
                var currentKey = StripeConfiguration.ApiKey;
                var isCurrentlyTest = currentKey?.StartsWith("sk_test_") == true;
                var currentMode = isCurrentlyTest ? "test" : "production";
                
                string? newKey = null;
                string newMode;
                
                if (targetMode == "toggle")
                {
                    // Alternar entre test y production
                    newMode = isCurrentlyTest ? "production" : "test";
                    newKey = newMode == "test" 
                        ? GetStripeTestKey()
                        : GetStripeProductionKey();
                }
                else if (targetMode == "test")
                {
                    newMode = "test";
                    newKey = GetStripeTestKey();
                }
                else if (targetMode == "production")
                {
                    newMode = "production";
                    newKey = GetStripeProductionKey();
                }
                else
                {
                    return BadRequest(new { message = "Invalid mode. Use 'test', 'production', or 'toggle'" });
                }
                
                if (string.IsNullOrEmpty(newKey))
                {
                    return BadRequest(new { 
                        message = $"Stripe {newMode} key not found. Configure 'Stripe:SecretKey' for production or 'Stripe:SecretKeyTest' for test mode." 
                    });
                }
                
                // Actualizar StripeConfiguration
                StripeConfiguration.ApiKey = newKey;
                _currentStripeMode = newMode;
                
                // Actualizar en SubscriptionController y otros lugares si es necesario
                // Nota: Esto requiere que esos controladores también usen StripeConfiguration.ApiKey
                
                var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                
                return Ok(new
                {
                    message = $"Stripe mode changed from {currentMode} to {newMode}",
                    previousMode = currentMode,
                    currentMode = newMode,
                    keyPrefix = newKey.Substring(0, Math.Min(10, newKey.Length)) + "...",
                    changedBy = userId,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to toggle Stripe mode", error = ex.Message });
            }
        }
        
        // Helper para obtener clave de producción
        private string? GetStripeProductionKey()
        {
            _logger.LogInformation("[StripeMode] Buscando clave de PRODUCCIÓN...");
            
            // 1. PRIMERO: Intentar desde Secret Manager directamente (stripe-secret-key sin -dev)
            // Esto asegura que obtenemos la clave de producción incluso si en desarrollo se cargó la de test
            _logger.LogInformation("[StripeMode] Intentando obtener desde Secret Manager: stripe-secret-key (producción)");
            var fromSecretManager = GetSecretValueFromSecretManager("stripe-secret-key");
            if (!string.IsNullOrEmpty(fromSecretManager) && !fromSecretManager.StartsWith("sk_test_"))
            {
                _logger.LogInformation("[StripeMode] ✅ Clave de producción encontrada en Secret Manager");
                return fromSecretManager;
            }
            else if (!string.IsNullOrEmpty(fromSecretManager))
            {
                _logger.LogWarning($"[StripeMode] ⚠️ Secreto stripe-secret-key es de test, no producción: {fromSecretManager.Substring(0, Math.Min(10, fromSecretManager.Length))}...");
            }
            
            // 2. Intentar desde configuración (puede tener la clave de test en desarrollo)
            var fromConfig = _configuration["Stripe:SecretKey"];
            _logger.LogInformation($"[StripeMode] Config Stripe:SecretKey: {(string.IsNullOrEmpty(fromConfig) ? "vacío" : fromConfig.Substring(0, Math.Min(10, fromConfig.Length)) + "...")}");
            
            if (!string.IsNullOrEmpty(fromConfig) && !fromConfig.StartsWith("sk_test_"))
            {
                _logger.LogInformation("[StripeMode] ✅ Clave de producción encontrada en configuración");
                return fromConfig;
            }
            else if (!string.IsNullOrEmpty(fromConfig))
            {
                _logger.LogWarning("[StripeMode] ⚠️ Config tiene clave de test, no producción");
            }
            
            // 3. Intentar desde variable de entorno
            _logger.LogInformation("[StripeMode] Intentando obtener desde variable de entorno: STRIPE_SECRET_KEY");
            var fromEnv = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");
            if (!string.IsNullOrEmpty(fromEnv) && !fromEnv.StartsWith("sk_test_"))
            {
                _logger.LogInformation("[StripeMode] ✅ Clave de producción encontrada en variable de entorno");
                return fromEnv;
            }
            else if (!string.IsNullOrEmpty(fromEnv))
            {
                _logger.LogWarning("[StripeMode] ⚠️ Variable de entorno tiene clave de test, no producción");
            }
            
            _logger.LogWarning("[StripeMode] ❌ No se encontró clave de producción en ningún lugar");
            return null;
        }
        
        // Helper para obtener clave de test
        private string? GetStripeTestKey()
        {
            // 1. Intentar desde configuración
            var fromConfig = _configuration["Stripe:SecretKeyTest"];
            if (!string.IsNullOrEmpty(fromConfig) && fromConfig.StartsWith("sk_test_"))
                return fromConfig;
            
            // 2. Intentar desde Secret Manager (stripe-secret-key-dev)
            var fromSecretManager = GetSecretValueFromSecretManager("stripe-secret-key-dev");
            if (!string.IsNullOrEmpty(fromSecretManager) && fromSecretManager.StartsWith("sk_test_"))
                return fromSecretManager;
            
            // 3. Intentar desde variable de entorno
            var fromEnv = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY_TEST");
            if (!string.IsNullOrEmpty(fromEnv) && fromEnv.StartsWith("sk_test_"))
                return fromEnv;
            
            // 4. Fallback: buscar cualquier clave que empiece con sk_test_ en configuración
            var allKeys = _configuration.GetSection("Stripe").GetChildren();
            foreach (var key in allKeys)
            {
                var value = key.Value;
                if (!string.IsNullOrEmpty(value) && value.StartsWith("sk_test_"))
                    return value;
            }
            
            return null;
        }
        
        /// <summary>
        /// Obtiene la lista de webhooks configurados en Stripe
        /// </summary>
        [HttpGet("stripe/webhooks")]
        public async Task<IActionResult> GetStripeWebhooks([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            try
            {
                // Validar parámetros
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                var service = new Stripe.WebhookEndpointService();
                var options = new Stripe.WebhookEndpointListOptions
                {
                    Limit = 100 // Stripe permite máximo 100 por request
                };
                
                var webhooksResponse = await service.ListAsync(options);
                var allWebhooks = webhooksResponse.Data.Select(w => new
                {
                    id = w.Id,
                    url = w.Url,
                    status = w.Status, // "enabled" o "disabled"
                    enabled = w.Status == "enabled", // Convertir status a boolean
                    apiVersion = w.ApiVersion,
                    description = w.Description,
                    created = w.Created,
                    // Determinar si es de test o producción basado en el ID
                    isTest = w.Id.StartsWith("we_") && !w.Id.StartsWith("we_live_")
                }).ToList();

                var totalCount = allWebhooks.Count;
                var paginatedWebhooks = allWebhooks
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();
                
                return Ok(new
                {
                    webhooks = paginatedWebhooks,
                    pagination = new
                    {
                        page,
                        pageSize,
                        totalCount,
                        totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                        hasNextPage = page * pageSize < totalCount,
                        hasPreviousPage = page > 1
                    }
                });
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Error obteniendo webhooks de Stripe");
                return StatusCode(500, new { message = "Error obteniendo webhooks de Stripe", error = ex.Message });
            }
        }

        /// <summary>
        /// Crea o actualiza un webhook en Stripe
        /// </summary>
        [HttpPost("stripe/webhooks")]
        public async Task<IActionResult> CreateOrUpdateWebhook([FromBody] CreateWebhookRequest request)
        {
            try
            {
                var service = new Stripe.WebhookEndpointService();
                
                // Validar URL
                if (string.IsNullOrEmpty(request.url) || !Uri.TryCreate(request.url, UriKind.Absolute, out _))
                {
                    return BadRequest(new { message = "URL inválida" });
                }
                
                // Si hay un ID, actualizar el webhook existente
                if (!string.IsNullOrEmpty(request.webhookId))
                {
                    var updateOptions = new Stripe.WebhookEndpointUpdateOptions
                    {
                        Url = request.url,
                        Description = request.description
                    };
                    
                    // Nota: Los webhooks se crean habilitados por defecto
                    // Para deshabilitarlos, se debe eliminar y recrear, o usar el Dashboard de Stripe
                    
                    // Agregar eventos si se proporcionan
                    if (request.events != null && request.events.Any())
                    {
                        updateOptions.EnabledEvents = request.events;
                    }
                    
                    var webhook = await service.UpdateAsync(request.webhookId, updateOptions);
                    
                    _logger.LogInformation($"Webhook actualizado: {webhook.Id} -> {webhook.Url}");
                    
                    return Ok(new
                    {
                        message = "Webhook actualizado exitosamente",
                        webhook = new
                        {
                            id = webhook.Id,
                            url = webhook.Url,
                            status = webhook.Status,
                            enabled = webhook.Status == "enabled",
                            secret = webhook.Secret // ⚠️ IMPORTANTE: Este es el signing secret que necesitas configurar
                        }
                    });
                }
                else
                {
                    // Crear nuevo webhook
                    var createOptions = new Stripe.WebhookEndpointCreateOptions
                    {
                        Url = request.url,
                        EnabledEvents = request.events ?? new List<string>
                        {
                            "account.updated",
                            "account.application.authorized",
                            "account.application.deauthorized",
                            "checkout.session.completed",
                            "payment_intent.succeeded",
                            "payment_intent.payment_failed"
                        },
                        Description = request.description ?? "Webhook desde Admin Panel"
                    };
                    
                    // Nota: Los webhooks se crean habilitados por defecto en Stripe
                    
                    var webhook = await service.CreateAsync(createOptions);
                    
                    _logger.LogInformation($"Webhook creado: {webhook.Id} -> {webhook.Url}");
                    
                    return Ok(new
                    {
                        message = "Webhook creado exitosamente",
                        webhook = new
                        {
                            id = webhook.Id,
                            url = webhook.Url,
                            status = webhook.Status,
                            enabled = webhook.Status == "enabled",
                            secret = webhook.Secret // ⚠️ IMPORTANTE: Este es el signing secret que necesitas configurar
                        },
                        instructions = new
                        {
                            message = "IMPORTANTE: Configura el signing secret en Secret Manager",
                            secret = webhook.Secret,
                            steps = new[]
                            {
                                $"1. Copia el secret: {webhook.Secret}",
                                "2. Configúralo en Google Cloud Secret Manager como 'stripe-webhook-secret' (producción) o 'stripe-webhook-secret-dev' (desarrollo)",
                                "3. Reinicia la aplicación para que cargue el nuevo secret"
                            }
                        }
                    });
                }
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Error creando/actualizando webhook de Stripe");
                return StatusCode(500, new { message = "Error creando/actualizando webhook de Stripe", error = ex.Message, stripeError = ex.StripeError?.Message });
            }
        }

        /// <summary>
        /// Elimina un webhook de Stripe
        /// </summary>
        [HttpDelete("stripe/webhooks/{webhookId}")]
        public async Task<IActionResult> DeleteWebhook(string webhookId)
        {
            try
            {
                var service = new Stripe.WebhookEndpointService();
                var deleted = await service.DeleteAsync(webhookId);
                
                _logger.LogInformation($"Webhook eliminado: {webhookId}");
                
                return Ok(new
                {
                    message = "Webhook eliminado exitosamente",
                    deleted = deleted.Deleted
                });
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Error eliminando webhook de Stripe");
                return StatusCode(500, new { message = "Error eliminando webhook de Stripe", error = ex.Message });
            }
        }

        // Helper para obtener secretos desde Secret Manager (si está disponible)
        private string? GetSecretValueFromSecretManager(string secretName)
        {
            try
            {
                // Crear cliente de Secret Manager (usa Application Default Credentials)
                var client = Google.Cloud.SecretManager.V1.SecretManagerServiceClient.Create();
                var projectId = "grup-441318";
                var secretPath = $"projects/{projectId}/secrets/{secretName}/versions/latest";
                
                try
                {
                    var secretVersion = client.AccessSecretVersion(secretPath);
                    return secretVersion.Payload.Data.ToStringUtf8();
                }
                catch (Grpc.Core.RpcException)
                {
                    // Secreto no encontrado o Secret Manager no disponible
                    return null;
                }
            }
            catch
            {
                // Secret Manager no disponible (sin credenciales o error de conexión)
                return null;
            }
        }
    }
    
    public class ToggleStripeModeRequest
    {
        public string? mode { get; set; } // "test", "production", o "toggle"
    }
    
    public class CreateWebhookRequest
    {
        public string? webhookId { get; set; } // Si se proporciona, actualiza el webhook existente
        public string url { get; set; } = string.Empty;
        public bool? enabled { get; set; }
        public string? description { get; set; }
        public List<string>? events { get; set; } // Eventos a escuchar (ej: ["account.updated", "checkout.session.completed"])
    }
}
