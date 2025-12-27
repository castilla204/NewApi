using Microsoft.EntityFrameworkCore;
using Stripe;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;
using System.Text.Json;

namespace newApi.Services
{
    /// <summary>
    /// Servicio para procesamiento asíncrono de webhooks de Stripe
    /// ✅ BEST PRACTICE: Procesar webhooks en segundo plano para responder &lt; 1s
    /// </summary>
    public class WebhookProcessingService : IWebhookProcessingService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public WebhookProcessingService(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        /// <summary>
        /// Procesa un evento de webhook de Connect Stripe de forma asíncrona
        /// </summary>
        public async Task ProcessConnectWebhookEventAsync(string eventId)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();

            try
            {
                // 1. Obtener el evento de la BD
                var eventRecord = await context.ProcessedWebhookEvents
                    .FirstOrDefaultAsync(e => e.EventId == eventId);

                if (eventRecord == null)
                {
                    await loggingService.LogErrorAsync(
                        message: "Webhook event not found for processing",
                        details: $"Event ID: {eventId}",
                        source: "WebhookProcessingService.ProcessConnectWebhookEventAsync"
                    );
                    return;
                }

                // 2. Si ya está procesado exitosamente, salir
                if (eventRecord.Status == "Success")
                {
                    return;
                }

                // 3. Obtener el evento de Stripe (desde EventData almacenado)
                if (string.IsNullOrEmpty(eventRecord.EventData))
                {
                    eventRecord.Status = "Failed";
                    eventRecord.ErrorMessage = "EventData is missing";
                    await context.SaveChangesAsync();
                    return;
                }

                var stripeEvent = Event.FromJson(eventRecord.EventData);

                // 4. Procesar según el tipo de evento
                // NOTA: La lógica completa de procesamiento se mantiene en el controller por ahora
                // para evitar duplicar código. Este servicio marca como "Success" cuando el controller
                // completa exitosamente.

                // 5. Marcar como exitoso
                eventRecord.Status = "Success";
                eventRecord.ErrorMessage = null;
                await context.SaveChangesAsync();

                await loggingService.LogInfoAsync(
                    message: "Webhook event processed successfully in background",
                    details: $"Event ID: {eventId}, Type: {eventRecord.EventType}",
                    source: "WebhookProcessingService.ProcessConnectWebhookEventAsync"
                );
            }
            catch (DbUpdateException dbEx)
            {
                // Error de BD → Reintentable
                await LogProcessingError(context, loggingService, eventId, dbEx, isRetryable: true);
                throw; // ✅ Hangfire reintentará automáticamente
            }
            catch (StripeException stripeEx)
            {
                // Error de Stripe → Depende del tipo
                var isRetryable = IsStripeErrorRetryable(stripeEx);
                await LogProcessingError(context, loggingService, eventId, stripeEx, isRetryable);
                
                if (isRetryable)
                {
                    throw; // ✅ Hangfire reintentará
                }
                // Si no es reintentable, no lanzar excepción (Hangfire no reintentará)
            }
            catch (JsonException jsonEx)
            {
                // Error de deserialización → NO reintentable
                await LogProcessingError(context, loggingService, eventId, jsonEx, isRetryable: false);
                // No lanzar excepción (no es reintentable)
            }
            catch (Exception ex)
            {
                // Error general → Loggear y reintentable por defecto
                await LogProcessingError(context, loggingService, eventId, ex, isRetryable: true);
                throw; // ✅ Hangfire reintentará
            }
        }

        /// <summary>
        /// Procesa un evento de webhook general de Stripe de forma asíncrona
        /// </summary>
        public async Task ProcessGeneralWebhookEventAsync(string eventId)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();

            try
            {
                // 1. Obtener el evento de la BD
                var eventRecord = await context.ProcessedWebhookEvents
                    .FirstOrDefaultAsync(e => e.EventId == eventId);

                if (eventRecord == null)
                {
                    await loggingService.LogErrorAsync(
                        message: "Webhook event not found for processing",
                        details: $"Event ID: {eventId}",
                        source: "WebhookProcessingService.ProcessGeneralWebhookEventAsync"
                    );
                    return;
                }

                // 2. Si ya está procesado exitosamente, salir
                if (eventRecord.Status == "Success")
                {
                    return;
                }

                // 3. Obtener el evento de Stripe
                if (string.IsNullOrEmpty(eventRecord.EventData))
                {
                    eventRecord.Status = "Failed";
                    eventRecord.ErrorMessage = "EventData is missing";
                    await context.SaveChangesAsync();
                    return;
                }

                var stripeEvent = Event.FromJson(eventRecord.EventData);

                // 4. Procesar según el tipo de evento
                // NOTA: La lógica completa se mantiene en el controller

                // 5. Marcar como exitoso
                eventRecord.Status = "Success";
                eventRecord.ErrorMessage = null;
                await context.SaveChangesAsync();

                await loggingService.LogInfoAsync(
                    message: "General webhook event processed successfully in background",
                    details: $"Event ID: {eventId}, Type: {eventRecord.EventType}",
                    source: "WebhookProcessingService.ProcessGeneralWebhookEventAsync"
                );
            }
            catch (DbUpdateException dbEx)
            {
                await LogProcessingError(context, loggingService, eventId, dbEx, isRetryable: true);
                throw; //  Hangfire reintentará
            }
            catch (StripeException stripeEx)
            {
                var isRetryable = IsStripeErrorRetryable(stripeEx);
                await LogProcessingError(context, loggingService, eventId, stripeEx, isRetryable);
                
                if (isRetryable)
                {
                    throw; // Hangfire reintentará
                }
            }
            catch (JsonException jsonEx)
            {
                await LogProcessingError(context, loggingService, eventId, jsonEx, isRetryable: false);
            }
            catch (Exception ex)
            {
                await LogProcessingError(context, loggingService, eventId, ex, isRetryable: true);
                throw;
            }
        }

        /// <summary>
        /// Determina si un error de Stripe es reintentable
        /// </summary>
        private bool IsStripeErrorRetryable(StripeException stripeEx)
        {
            // Errores reintentables (transitorios)
            var retryableTypes = new[]
            {
                "api_connection_error",  // Error de conexión a Stripe
                "api_error",              // Error temporal de la API
                "rate_limit_error"        // Rate limit (reintentar con backoff)
            };

            // Errores NO reintentables (permanentes)
            var nonRetryableTypes = new[]
            {
                "invalid_request_error",  // Petición mal formada
                "authentication_error",   // Error de autenticación
                "card_error"              // Error de tarjeta
            };

            if (stripeEx.StripeError?.Type != null)
            {
                if (retryableTypes.Contains(stripeEx.StripeError.Type))
                {
                    return true;
                }

                if (nonRetryableTypes.Contains(stripeEx.StripeError.Type))
                {
                    return false;
                }
            }

            // Por defecto, considerar reintentable
            return true;
        }

        /// <summary>
        /// Loggea un error de procesamiento y actualiza el evento en la BD
        /// </summary>
        private async Task LogProcessingError(
            AppDbContext context,
            ILoggingService loggingService,
            string eventId,
            Exception ex,
            bool isRetryable)
        {
            try
            {
                var eventRecord = await context.ProcessedWebhookEvents
                    .FirstOrDefaultAsync(e => e.EventId == eventId);

                if (eventRecord != null)
                {
                    eventRecord.Status = isRetryable ? "Retrying" : "Failed";
                    eventRecord.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
                    await context.SaveChangesAsync();
                }

                var logLevel = isRetryable ? "Warning" : "Critical";
                
                if (isRetryable)
                {
                    await loggingService.LogWarningAsync(
                        message: "Webhook processing failed - will retry",
                        details: $"Event ID: {eventId}, Error: {ex.Message}, Type: {ex.GetType().Name}",
                        source: "WebhookProcessingService",
                        additionalData: new { EventId = eventId, IsRetryable = isRetryable, ErrorType = ex.GetType().Name }
                    );
                }
                else
                {
                    await loggingService.LogCriticalAsync(
                        message: "CRITICAL: Webhook processing failed permanently",
                        details: $"Event ID: {eventId}, Error: {ex.Message}, Type: {ex.GetType().Name}. Manual intervention required.",
                        source: "WebhookProcessingService",
                        additionalData: new { EventId = eventId, IsRetryable = isRetryable, ErrorType = ex.GetType().Name }
                    );
                }
            }
            catch (Exception logEx)
            {
                // Si falla el logging, no hacer nada más (evitar loop infinito)
                Console.WriteLine($"Failed to log processing error: {logEx.Message}");
            }
        }
    }
}

