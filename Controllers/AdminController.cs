using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.Services;
using Stripe;
using System.Security.Claims;
using Microsoft.Extensions.Logging;

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
        private readonly IStripeConfigService _stripeConfigService;
        private readonly ILogger<AdminController> _logger;
        
        public AdminController(
            AppDbContext context, 
            StripeRefundService refundService,
            IStripeConfigService stripeConfigService,
            ILogger<AdminController> logger)
        {
            _context = context;
            _refundService = refundService;
            _stripeConfigService = stripeConfigService;
            _logger = logger;
        }

        /// <summary>
        /// Obtener el modo actual de Stripe (development/production)
        /// </summary>
        [HttpGet("stripe/mode")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetStripeMode()
        {
            try
            {
                var mode = await _stripeConfigService.GetStripeModeAsync();
                var setting = await _context.SystemSettings.FirstOrDefaultAsync();
                
                return Ok(new
                {
                    mode = mode,
                    changedAt = setting?.StripeModeChangedAt,
                    changedByUserId = setting?.StripeModeChangedByUserId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error obteniendo modo Stripe");
                return StatusCode(500, new { message = "Error obteniendo modo Stripe", error = ex.Message });
            }
        }

        /// <summary>
        /// Cambiar el modo de Stripe entre development y production
        /// </summary>
        [HttpPost("stripe/mode")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> SetStripeMode([FromBody] SetStripeModeDto request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.Mode))
                {
                    return BadRequest(new { message = "El modo es requerido" });
                }

                if (request.Mode != "development" && request.Mode != "production")
                {
                    return BadRequest(new { message = "El modo debe ser 'development' o 'production'" });
                }

                var userId = int.Parse(User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value ?? "0");
                
                var success = await _stripeConfigService.SetStripeModeAsync(request.Mode, userId);
                
                if (success)
                {
                    // Recargar las claves de Stripe con el nuevo modo
                    // Esto requiere reiniciar la aplicación o recargar la configuración
                    _logger.LogWarning($"⚠️ Modo Stripe cambiado a {request.Mode}. Se requiere reiniciar la aplicación para aplicar los cambios.");
                    
                    return Ok(new
                    {
                        message = $"Modo Stripe cambiado a {request.Mode}",
                        mode = request.Mode,
                        warning = "Se requiere reiniciar la aplicación para aplicar los cambios completamente"
                    });
                }

                return StatusCode(500, new { message = "Error cambiando modo Stripe" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cambiando modo Stripe");
                return StatusCode(500, new { message = "Error cambiando modo Stripe", error = ex.Message });
            }
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
    }

    public class SetStripeModeDto
    {
        public string Mode { get; set; } = string.Empty;
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
