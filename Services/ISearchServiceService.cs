using Google.Cloud.Storage.V1;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;


namespace newApi.Services
{
    public interface ISearchServiceService
    {



        Task<IEnumerable<SearchServiceDetailDto>> GetAllServices(
     int categoryId,
     int serviceTypeId,
     string latitude,
     string longitude,
     int locationRange);

        Task<ExpertMapResponseDto> GetMapExperts(
            int categoryId, 
            int serviceTypeId,
            decimal? northeastLat = null,
            decimal? northeastLng = null,
            decimal? southwestLat = null,
            decimal? southwestLng = null,
            int? zoom = null,
            int limit = 100);

        Task<SearchServiceDetailDto> GetServiceByHireId(int id);

        Task<(IEnumerable<SearchServiceResponseDto> services, int totalCount)> GetExpertServices(int expertId, int? serviceTypeId = null, int page = 1, int pageSize = 20);

        Task<SearchServiceDetailDto> GetServiceById(int id);

        Task<(bool Success, SearchService Service, List<string> ImageUrls)> CreateSearchService(
            int userId,
            CreateSearchServiceRequestDto request);

        Task<(bool Success, SearchService NewService, List<string> ImageUrls)> UpdateSearchService(
            int userId,
            UpdateSearchServiceRequestDto request);

        Task<bool> DeleteSearchService(int serviceId, int userId);

        Task<(IEnumerable<SearchServiceDetailDto> services, int totalCount)> GetNearbyServices(
            string? latitude,
            string? longitude,
            string? countryCode,
            int locationRange,
            int page = 1,
            int pageSize = 20);

        Task<(IEnumerable<SearchServiceDetailDto> services, int totalCount)> GetPopularServices(
            int page = 1,
            int pageSize = 20);

    }
}