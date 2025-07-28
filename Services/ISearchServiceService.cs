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

        Task<IEnumerable<SearchServiceResponseDto>> GetExpertServices(int expertId, int? serviceTypeId = null);

        Task<SearchServiceDetailDto> GetServiceById(int id);

        Task<(bool Success, SearchService Service, List<string> ImageUrls)> CreateSearchService(
            int userId,
            CreateSearchServiceRequestDto request);



    }
}