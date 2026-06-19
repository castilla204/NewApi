namespace newApi.Services
{
    public interface IPaymentCaptureService
    {
        Task EnsureCapturedAsync(string paymentIntentId, int userId, int serviceId, int searchHireId);
    }
}
