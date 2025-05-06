using Stripe.Checkout;
using newApi.DataLayer.Models.DTOs;

namespace newApi.Services
{
    public interface ISearchHireService
    {
        Task<Session> CreateCheckoutSession(int userId, int serviceId);
        Task<bool> HandleCheckoutSession(Session session);
        Task<IEnumerable<SearchHireResponseDto>> GetClientHires(int userId);
        Task<IEnumerable<SearchHireResponseDto>> GetExpertHires(int userId);
        Task<bool> UpdateHireStatus(int userId, int hireId, string status);
    }
}