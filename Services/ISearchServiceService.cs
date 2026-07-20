using Google.Cloud.Storage.V1;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using System.Threading;


namespace newApi.Services
{
    public interface ISearchServiceService
    {



        Task<(IEnumerable<SearchServiceDetailDto> services, int totalCount)> GetAllServices(
     int categoryId,
     int serviceTypeId,
     string latitude,
     string longitude,
     int locationRange,
     int page = 1,
     int pageSize = 50,
     CancellationToken cancellationToken = default);

        Task<ExpertMapResponseDto> GetMapExperts(
            int categoryId, 
            int serviceTypeId,
            decimal? northeastLat = null,
            decimal? northeastLng = null,
            decimal? southwestLat = null,
            decimal? southwestLng = null,
            int? zoom = null,
            int limit = 100,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Obtiene marcadores ultra ligeros para el mapa (solo coordenadas + precio)
        /// Optimizado para carga inicial rápida - 10-50x más rápido que GetMapExperts
        /// </summary>
        Task<MapMarkersResponseDto> GetMapMarkers(
            int categoryId,
            int serviceTypeId,
            decimal? northeastLat = null,
            decimal? northeastLng = null,
            decimal? southwestLat = null,
            decimal? southwestLng = null,
            int? zoom = null,
            int limit = 500,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Obtiene información básica para el sidebar (primera imagen + info básica)
        /// Optimizado para mostrar cards sin cargar toda la información
        /// </summary>
        Task<MapSidebarResponseDto> GetMapSidebar(
            int[] serviceIds,
            CancellationToken cancellationToken = default);

        Task<(IEnumerable<SearchServiceDetailDto> services, int totalCount)> GetMapExpertsWithDetails(
            int categoryId, 
            int serviceTypeId,
            decimal northeastLat,
            decimal northeastLng,
            decimal southwestLat,
            decimal southwestLng,
            int? zoom = null,
            int limit = 100,
            int page = 1,
            int pageSize = 50,
            CancellationToken cancellationToken = default);

        Task<SearchServiceDetailDto> GetServiceByHireId(int id);

        Task<(IEnumerable<SearchServiceResponseDto> services, int totalCount)> GetExpertServices(int expertId, int? serviceTypeId = null, int page = 1, int pageSize = 20, bool includeInactive = false);

        Task<SearchServiceDetailDto> GetServiceById(int id);

        Task<(bool Success, SearchService Service, List<string> ImageUrls)> CreateSearchService(
            int userId,
            CreateSearchServiceRequestDto request);

        Task<(bool Success, SearchService NewService, List<string> ImageUrls)> UpdateSearchService(
            int userId,
            UpdateSearchServiceRequestDto request);

        Task<bool> DeleteSearchService(int serviceId, int userId);

        Task<(bool Success, string? ErrorCode)> ReactivateSearchService(int serviceId, int userId);

        Task<(IEnumerable<SearchServiceHomepageDto> services, int totalCount)> GetNearbyServices(
            string? latitude,
            string? longitude,
            string? countryCode,
            int locationRange,
            int? categoryId = null,  // ✅ Filtro por categoría
            int page = 1,
            int pageSize = 20,
            CancellationToken cancellationToken = default);

        Task<(IEnumerable<SearchServiceHomepageDto> services, int totalCount)> GetPopularServices(
            int? categoryId = null,  // ✅ Filtro por categoría
            int page = 1,
            int pageSize = 20,
            CancellationToken cancellationToken = default);

        Task<Dictionary<string, (IEnumerable<SearchServiceHomepageDto> services, int totalCount, string categoryName, string country)>> GetServicesByCategoryAndCountry(
            int maxSections = 10,
            int servicesPerSection = 20,
            string[]? targetCategories = null,
            string[]? targetCountries = null,
            CancellationToken cancellationToken = default);

    }
}