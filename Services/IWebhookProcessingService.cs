using Stripe;

namespace newApi.Services
{
    /// <summary>
    /// Servicio para procesamiento asíncrono de webhooks de Stripe
    /// </summary>
    public interface IWebhookProcessingService
    {
        /// <summary>
        /// Procesa un evento de webhook de Stripe de forma asíncrona
        /// </summary>
        Task ProcessConnectWebhookEventAsync(string eventId);
        
        /// <summary>
        /// Procesa un evento de webhook general de Stripe de forma asíncrona
        /// </summary>
        Task ProcessGeneralWebhookEventAsync(string eventId);
    }
}

