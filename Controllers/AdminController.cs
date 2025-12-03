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
        /// Alternar el modo de Stripe entre development y production
        /// </summary>
        [HttpPost("stripe/toggle-mode")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ToggleStripeMode()
        {
            try
            {
                var currentMode = await _stripeConfigService.GetStripeModeAsync();
                var newMode = currentMode == "development" ? "production" : "development";
                
                var userId = int.Parse(User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value ?? "0");
                
                var success = await _stripeConfigService.SetStripeModeAsync(newMode, userId);
                
                if (success)
                {
                    _logger.LogWarning($"⚠️ Modo Stripe cambiado de {currentMode} a {newMode}. Se requiere reiniciar la aplicación para aplicar los cambios.");
                    
                    return Ok(new
                    {
                        message = $"Modo Stripe cambiado de {currentMode} a {newMode}",
                        previousMode = currentMode,
                        newMode = newMode,
                        warning = "Se requiere reiniciar la aplicación para aplicar los cambios completamente"
                    });
                }

                return StatusCode(500, new { message = "Error cambiando modo Stripe" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error alternando modo Stripe");
                return StatusCode(500, new { message = "Error alternando modo Stripe", error = ex.Message });
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
        /// ⚠️ TEMPORAL: Aplicar migración de StripeMode a SystemSettings
        /// Este endpoint es temporal y solo debe usarse para aplicar la migración una vez
        /// </summary>
        [HttpPost("stripe/apply-migration")]
        public async Task<IActionResult> ApplyStripeModeMigration()
        {
            try
            {
                _logger.LogInformation("🔧 Iniciando aplicación de migración StripeMode");

                // Verificar estado actual antes de aplicar
                var existingColumns = await _context.Database.SqlQueryRaw<string>(
                    @"SELECT column_name 
                      FROM information_schema.columns 
                      WHERE table_name = 'SystemSettings' 
                      AND column_name IN ('StripeMode', 'StripeModeChangedAt', 'StripeModeChangedByUserId')
                      ORDER BY column_name"
                ).ToListAsync();

                if (existingColumns.Count == 3)
                {
                    _logger.LogInformation("✅ Las columnas StripeMode ya existen");
                    return Ok(new
                    {
                        message = "Las columnas ya existen",
                        existingColumns = existingColumns,
                        timestamp = DateTime.UtcNow
                    });
                }

                _logger.LogInformation($"📊 Columnas existentes antes de migración: {string.Join(", ", existingColumns)}");

                // SQL para agregar las columnas
                var sql = @"
DO $$ 
BEGIN
    -- Add StripeMode column
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                  WHERE table_schema = 'public' 
                  AND table_name = 'SystemSettings' 
                  AND column_name = 'StripeMode') THEN
        ALTER TABLE ""SystemSettings"" 
        ADD COLUMN ""StripeMode"" character varying(20) NOT NULL DEFAULT 'production';
        RAISE NOTICE 'Columna StripeMode agregada';
    ELSE
        RAISE NOTICE 'Columna StripeMode ya existe';
    END IF;

    -- Add StripeModeChangedAt column
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                  WHERE table_schema = 'public' 
                  AND table_name = 'SystemSettings' 
                  AND column_name = 'StripeModeChangedAt') THEN
        ALTER TABLE ""SystemSettings"" 
        ADD COLUMN ""StripeModeChangedAt"" timestamp with time zone NULL;
        RAISE NOTICE 'Columna StripeModeChangedAt agregada';
    ELSE
        RAISE NOTICE 'Columna StripeModeChangedAt ya existe';
    END IF;

    -- Add StripeModeChangedByUserId column
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                  WHERE table_schema = 'public' 
                  AND table_name = 'SystemSettings' 
                  AND column_name = 'StripeModeChangedByUserId') THEN
        ALTER TABLE ""SystemSettings"" 
        ADD COLUMN ""StripeModeChangedByUserId"" integer NULL;
        RAISE NOTICE 'Columna StripeModeChangedByUserId agregada';
    ELSE
        RAISE NOTICE 'Columna StripeModeChangedByUserId ya existe';
    END IF;
END $$;
";

                _logger.LogInformation("🚀 Ejecutando SQL de migración...");
                var rowsAffected = await _context.Database.ExecuteSqlRawAsync(sql);
                _logger.LogInformation($"✅ SQL ejecutado. Filas afectadas: {rowsAffected}");
                
                // Verificar que las columnas se agregaron
                var columns = await _context.Database.SqlQueryRaw<string>(
                    @"SELECT column_name 
                      FROM information_schema.columns 
                      WHERE table_schema = 'public' 
                      AND table_name = 'SystemSettings' 
                      AND column_name LIKE 'Stripe%'
                      ORDER BY column_name"
                ).ToListAsync();

                _logger.LogInformation($"✅ Migración completada. Columnas encontradas: {string.Join(", ", columns)}");

                return Ok(new
                {
                    message = "Migración aplicada exitosamente",
                    columns = columns,
                    columnsBefore = existingColumns,
                    rowsAffected = rowsAffected,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error aplicando migración de StripeMode");
                return StatusCode(500, new 
                { 
                    message = "Error aplicando migración", 
                    error = ex.Message,
                    innerException = ex.InnerException?.Message,
                    stackTrace = ex.StackTrace
                });
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
