namespace newApi.Services
{
    // TODO rename to ISearchHireTimeoutService: la lógica real procesa SearchHires en awaiting_client_decision tras 24h.
    public interface ISubscriptionService
    {
        Task<SubscriptionLimits> GetUserSubscriptionLimits(int userId);
        Task ProcessAwaitingClientDecisionAsync(); // Solo para uso manual/admin: procesa todos los que llevan más de 24h
        Task ProcessAwaitingClientDecisionAsync(int searchHireId); // Procesa un SearchHire específico después de 24h (usado por scheduled jobs)

    }

    public class SubscriptionLimits
    {
        public int MaxSearches { get; set; }
        public int MinSearchInterval { get; set; }
    }
}