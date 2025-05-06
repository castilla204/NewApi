using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels.newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    public interface ISearchServiceService
    {
        Task<IEnumerable<SearchServiceDetailDto>> GetAllServices();
        Task<IEnumerable<SearchServiceResponseDto>> GetExpertServices(int expertId);
        Task<SearchServiceDetailDto> GetServiceById(int id);
        Task<(bool success, SearchService service, List<string> imageUrls)> CreateSearchService(int userId, CreateSearchServiceRequestDto request);
    }
}