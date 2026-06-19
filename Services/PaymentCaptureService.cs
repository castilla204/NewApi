using Stripe;

namespace newApi.Services
{
    /// <summary>
    /// Captura del PaymentIntent (manual capture) extraída de SubscriptionController.
    /// REFACTOR PURO Tarea 1: misma lógica que el antiguo EnsurePaymentCapturedAsync
    /// (GET → si requires_capture: CaptureAsync con IdempotencyKey capture-{searchHireId};
    ///  si succeeded: return; otro estado: log critical + InvalidOperationException).
    /// Permite reutilizar la captura desde el confirm del vendedor (tareas futuras).
    /// </summary>
    public class PaymentCaptureService : IPaymentCaptureService
    {
        private readonly ILoggingService _loggingService;

        public PaymentCaptureService(ILoggingService loggingService)
        {
            _loggingService = loggingService;
        }

        public async Task EnsureCapturedAsync(string paymentIntentId, int userId, int serviceId, int searchHireId)
        {
            var paymentIntentService = new PaymentIntentService();
            PaymentIntent paymentIntent;

            try
            {
                paymentIntent = await paymentIntentService.GetAsync(paymentIntentId);
            }
            catch (StripeException ex)
            {
                await LogPaymentCaptureFailureAsync(paymentIntentId, userId, serviceId, $"Stripe error retrieving PaymentIntent: {ex.Message}", searchHireId, ex);
                throw;
            }

            if (paymentIntent.Status == "requires_capture")
            {
                try
                {
                    await paymentIntentService.CaptureAsync(
                        paymentIntentId,
                        null,
                        new RequestOptions { IdempotencyKey = $"capture-{searchHireId}" });
                }
                catch (StripeException ex)
                {
                    await LogPaymentCaptureFailureAsync(paymentIntentId, userId, serviceId, $"Stripe error capturing PaymentIntent: {ex.Message}", searchHireId, ex);
                    throw;
                }
            }
            else if (paymentIntent.Status == "succeeded")
            {
                return;
            }
            else
            {
                var message = $"PaymentIntent {paymentIntentId} is in '{paymentIntent.Status}' state and cannot be captured.";
                await LogPaymentCaptureFailureAsync(paymentIntentId, userId, serviceId, message, searchHireId);
                throw new InvalidOperationException(message);
            }
        }

        // ⚠️ REFACTOR Tarea 1: el source se fija explícitamente a
        // "SubscriptionController.EnsurePaymentCapturedAsync" para conservar EXACTAMENTE
        // el string que producía el [CallerMemberName] del helper original cuando se
        // invocaba desde EnsurePaymentCapturedAsync (sin cambio de comportamiento en logs).
        private async Task LogPaymentCaptureFailureAsync(string paymentIntentId, int userId, int serviceId, string failureReason, int? searchHireId = null, Exception? exception = null)
        {
            await _loggingService.LogCriticalAsync(
                message: "CRITICAL: Payment capture failure",
                details: $"Failed to capture PaymentIntent {paymentIntentId}. Reason: {failureReason}",
                userId: null,
                source: "SubscriptionController.EnsurePaymentCapturedAsync",
                relatedEntityType: "Payment",
                relatedEntityId: serviceId,
                additionalData: new
                {
                    PaymentIntentId = paymentIntentId,
                    SearchHireId = searchHireId,
                    ClientId = userId,
                    Reason = failureReason,
                    Exception = exception?.Message
                }
            );
        }
    }
}
