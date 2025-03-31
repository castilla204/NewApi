namespace newApi.Services
{
    public interface ISubscriptionService
    {
        Task<SubscriptionLimits> GetUserSubscriptionLimits(int userId);
    }

    public class SubscriptionLimits
    {
        public int MaxSearches { get; set; }
        public int MinSearchInterval { get; set; }
    }
}