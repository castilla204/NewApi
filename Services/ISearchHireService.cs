using Stripe.Checkout;
using newApi.DataLayer.Models.DTOs;

namespace newApi.Services
{
    public interface ISearchHireService
    {
        Task<(IEnumerable<SearchHireResponseDto> hires, int totalCount)> GetClientHires(int userId, int page, int pageSize);
        Task<(IEnumerable<SearchHireResponseDto> hires, int totalCount)> GetExpertHires(int userId, int page, int pageSize);
        Task<(bool Success, string ErrorMessage)> UpdateHireStatus(int userId, int hireId, string status);
    }
}