using Microsoft.EntityFrameworkCore;
using Google.Cloud.Storage.V1;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using newApi.DataLayer;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    public class SearchServiceService : ISearchServiceService
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SearchServiceService> _logger;
        private readonly StorageClient _storageClient;

        public SearchServiceService(
            AppDbContext context,
            IConfiguration configuration,
            ILogger<SearchServiceService> logger,
            StorageClient storageClient)
        {
            _context = context;
            _configuration = configuration;
            _logger = logger;
            _storageClient = storageClient;
        }

        public async Task<IEnumerable<SearchServiceDetailDto>> GetAllServices(
            int categoryId,
            int serviceTypeId,
            string latitude,
            string longitude,
            int locationRange)
        {
            try
            {
                _logger.LogInformation("Fetching services for CategoryId: {CategoryId}, ServiceTypeId: {ServiceTypeId}, Latitude: {Latitude}, Longitude: {Longitude}, LocationRange: {LocationRange}",
                    categoryId, serviceTypeId, latitude, longitude, locationRange);

                if (categoryId <= 0 || serviceTypeId <= 0)
                {
                    _logger.LogWarning("Invalid CategoryId: {CategoryId} or ServiceTypeId: {ServiceTypeId}", categoryId, serviceTypeId);
                    throw new ArgumentException("CategoryId and ServiceTypeId must be greater than 0");
                }

                if (string.IsNullOrEmpty(latitude) || string.IsNullOrEmpty(longitude) || locationRange <= 0)
                {
                    _logger.LogWarning("Missing or invalid location parameters: Latitude={Latitude}, Longitude={Longitude}, LocationRange={LocationRange}", latitude, longitude, locationRange);
                    throw new ArgumentException("Latitude, Longitude, and LocationRange are required and must be valid");
                }

                if (!decimal.TryParse(latitude, out var searchLatitude) || !decimal.TryParse(longitude, out var searchLongitude))
                {
                    _logger.LogWarning("Invalid Latitude or Longitude format: Latitude={Latitude}, Longitude={Longitude}", latitude, longitude);
                    throw new ArgumentException("Invalid coordinates provided");
                }

                // Fetch services with necessary includes
                var query = _context.SearchServices
                    .Where(ss => ss.CategoryId == categoryId && ss.ServiceTypeId == serviceTypeId)
                    .Include(ss => ss.Images)
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                    .Include(ss => ss.Category)
                    .Include(ss => ss.ServiceType);

                // Materialize the query to avoid LINQ translation issues
                var services = await query.ToListAsync();

                // Filter by distance in memory using CalculateDistance
                var filteredServices = services
                    .Where(ss => ss.ExpertProfile != null &&
                        CalculateDistance(searchLatitude, searchLongitude, ss.ExpertProfile.Latitude, ss.ExpertProfile.Longitude) <= locationRange)
                    .Select(ss => MapToDetailDto(ss))
                    .ToList();

                _logger.LogInformation("Retrieved {ServiceCount} services for CategoryId: {CategoryId}, ServiceTypeId: {ServiceTypeId}, Latitude: {Latitude}, Longitude: {Longitude}, LocationRange: {LocationRange}",
                    filteredServices.Count, categoryId, serviceTypeId, latitude, longitude, locationRange);
                return filteredServices;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving services with CategoryId: {CategoryId}, ServiceTypeId: {ServiceTypeId}, Latitude: {Latitude}, Longitude: {Longitude}, LocationRange: {LocationRange}",
                    categoryId, serviceTypeId, latitude, longitude, locationRange);
                throw;
            }
        }

        // Haversine formula for distance calculation
        public static decimal CalculateDistance(decimal lat1, decimal lon1, decimal? lat2, decimal? lon2)
        {
            if (!lat2.HasValue || !lon2.HasValue) return decimal.MaxValue;

            const double R = 6371; // Earth's radius in km
            var dLat = (double)(lat2.Value - lat1) * Math.PI / 180;
            var dLon = (double)(lon2.Value - lon1) * Math.PI / 180;
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos((double)lat1 * Math.PI / 180) * Math.Cos((double)lat2.Value * Math.PI / 180) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return (decimal)(R * c);
        }

        public async Task<IEnumerable<SearchServiceResponseDto>> GetExpertServices(int expertId, int? serviceTypeId = null)
        {
            try
            {
                _logger.LogInformation("Fetching expert services for ExpertId: {ExpertId}, ServiceTypeId: {ServiceTypeId}", expertId, serviceTypeId);
                IQueryable<SearchService> query = _context.SearchServices
                    .Where(ss => ss.ExpertProfileId == expertId);

                if (serviceTypeId.HasValue)
                {
                    query = query.Where(ss => ss.ServiceTypeId == serviceTypeId.Value);
                }

                query = query
                    .Include(ss => ss.Images)
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                    .Include(ss => ss.Category)
                    .Include(ss => ss.ServiceType);

                var services = await query
                    .Select(ss => MapToResponseDto(ss))
                    .ToListAsync();

                _logger.LogInformation("Retrieved {ServiceCount} expert services for ExpertId: {ExpertId}, ServiceTypeId: {ServiceTypeId}", services.Count, expertId, serviceTypeId);
                return services;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving expert services for ExpertId: {ExpertId}, ServiceTypeId: {ServiceTypeId}", expertId, serviceTypeId);
                throw;
            }
        }

        public async Task<SearchServiceDetailDto> GetServiceById(int id)
        {
            try
            {
                _logger.LogInformation("Fetching service with Id: {Id}", id);
                var service = await _context.SearchServices
                    .Include(ss => ss.Images)
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                    .Include(ss => ss.Category)
                    .Include(ss => ss.ServiceType)
                    .FirstOrDefaultAsync(ss => ss.Id == id);

                if (service == null)
                {
                    _logger.LogWarning("Service not found with Id: {Id}", id);
                }
                return service == null ? null : MapToDetailDto(service);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving service with Id: {Id}", id);
                throw;
            }
        }

        public async Task<(bool Success, SearchService Service, List<string> ImageUrls)> CreateSearchService(
            int userId,
            CreateSearchServiceRequestDto request)
        {
            try
            {
                _logger.LogInformation("Creating SearchService with ServiceTypeId: {ServiceTypeId}", request.ServiceTypeId);

                var serviceTypeExists = await _context.ServiceTypes.AnyAsync(st => st.Id == request.ServiceTypeId);
                if (!serviceTypeExists)
                {
                    _logger.LogError("Invalid ServiceTypeId: {ServiceTypeId}", request.ServiceTypeId);
                    return (false, null, null);
                }

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.Id == request.ExpertProfileId && ep.UserId == userId);
                if (expertProfile == null)
                {
                    _logger.LogError("ExpertProfileId {ExpertProfileId} not found or does not belong to user {UserId}", request.ExpertProfileId, userId);
                    return (false, null, null);
                }

                var category = await _context.Categories.FindAsync(request.CategoryId);
                if (category == null)
                {
                    _logger.LogError("Invalid CategoryId: {CategoryId}", request.CategoryId);
                    return (false, null, null);
                }

                var searchService = new SearchService
                {
                    ExpertProfileId = request.ExpertProfileId,
                    CategoryId = request.CategoryId,
                    ServiceTypeId = request.ServiceTypeId,
                    Price = request.Price,
                    Conditions = request.Conditions,
                    DurationInHours = request.DurationInHours ?? 0,
                    CreatedAt = DateTime.UtcNow
                };

                _context.SearchServices.Add(searchService);
                _logger.LogInformation("Attempting to save SearchService with ServiceTypeId: {ServiceTypeId}", searchService.ServiceTypeId);
                await _context.SaveChangesAsync();
                _logger.LogInformation("Successfully saved SearchService with Id: {ServiceId}", searchService.Id);

                var imageUrls = new List<string>();
                if (request.Images != null && request.Images.Any())
                {
                    var bucketName = _configuration["GoogleCloud:BucketName"];
                    foreach (var imageFile in request.Images)
                    {
                        var extension = Path.GetExtension(imageFile.FileName).ToLowerInvariant();
                        var uniqueFileName = $"{Guid.NewGuid()}{extension}";
                        var objectName = $"services/{uniqueFileName}";

                        using (var inputStream = imageFile.OpenReadStream())
                        using (var image = Image.Load(inputStream))
                        {
                            image.Mutate(x => x.Resize(new ResizeOptions
                            {
                                Size = new Size(200, 200),
                                Mode = ResizeMode.Max
                            }));

                            using (var outputStream = new MemoryStream())
                            {
                                image.SaveAsJpeg(outputStream);
                                outputStream.Position = 0;
                                await _storageClient.UploadObjectAsync(
                                    bucketName,
                                    objectName,
                                    "image/jpeg", // Explicitly define contentType
                                    outputStream // Use sourceStream parameter
                                );
                            }
                        }

                        var imageUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";
                        imageUrls.Add(imageUrl);

                        var searchServiceImage = new SearchServiceImage
                        {
                            SearchServiceId = searchService.Id,
                            ImageUrl = imageUrl,
                            ImageObjectName = objectName,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.SearchServiceImages.Add(searchServiceImage);
                    }
                    await _context.SaveChangesAsync();
                }

                return (true, searchService, imageUrls);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating search service with ServiceTypeId: {ServiceTypeId}", request.ServiceTypeId);
                throw;
            }
        }

        private static SearchServiceDetailDto MapToDetailDto(SearchService ss)
        {
            if (ss == null) return null;

            var baseDto = new SearchServiceResponseDto
            {
                Id = ss.Id,
                CategoryId = ss.CategoryId,
                ServiceTypeId = ss.ServiceTypeId,
                ServiceTypeName = ss.ServiceType?.Name ?? "Unknown Service Type",
                Price = ss.Price,
                Conditions = ss.Conditions,
                DurationInHours = ss.DurationInHours ?? 0,
                CreatedAt = ss.CreatedAt,
                ImageUrls = ss.Images?.Select(i => i.ImageUrl).ToList() ?? new List<string>()
            };

            ExpertProfileDto expertProfileDto = null;
            if (ss.ExpertProfile != null)
            {
                var userDto = ss.ExpertProfile.User != null ? new UserDto
                {
                    Name = ss.ExpertProfile.User.Name,
                    Email = ss.ExpertProfile.User.Email
                } : null;

                var reviews = ss.ExpertProfile.User?.ReviewsReceived?.Select(r => new ReviewDto
                {
                    Id = r.Id,
                    Score = r.Score,
                    Description = r.Description ?? "",
                    CreatedAt = r.CreatedAt
                }).ToList() ?? new List<ReviewDto>();

                expertProfileDto = new ExpertProfileDto
                {
                    Id = ss.ExpertProfile.Id,
                    ProfilePictureUrl = ss.ExpertProfile.ProfilePictureUrl,
                    Description = ss.ExpertProfile.Description,
                    CreatedAt = ss.ExpertProfile.CreatedAt,
                    User = userDto,
                    Reviews = reviews
                };
            }

            baseDto.Expert = expertProfileDto;

            var detailDto = new SearchServiceDetailDto
            {
                Id = baseDto.Id,
                CategoryId = baseDto.CategoryId,
                ServiceTypeId = baseDto.ServiceTypeId,
                ServiceTypeName = baseDto.ServiceTypeName,
                Price = baseDto.Price,
                Conditions = baseDto.Conditions,
                DurationInHours = baseDto.DurationInHours,
                CreatedAt = baseDto.CreatedAt,
                ImageUrls = baseDto.ImageUrls,
                Expert = baseDto.Expert,
                CategoryName = ss.Category?.Name ?? "Unknown Category",
                CompletedSearches = ss.ExpertProfile?.User?.SearchHiresAsExpert?.Count(sh => sh.Status == "Completed") ?? 0,
                AverageRating = ss.ExpertProfile?.User?.ReviewsReceived != null && ss.ExpertProfile.User.ReviewsReceived.Any()
                    ? ss.ExpertProfile.User.ReviewsReceived.Average(r => r.Score)
                    : 0
            };

            return detailDto;
        }

        private static SearchServiceResponseDto MapToResponseDto(SearchService ss)
        {
            var searchService = new SearchServiceResponseDto
            {
                Id = ss.Id,
                CategoryId = ss.CategoryId,
                ServiceTypeId = ss.ServiceTypeId,
                ServiceTypeName = ss.ServiceType?.Name ?? "Unknown Service Type",
                Price = ss.Price,
                Conditions = ss.Conditions,
                DurationInHours = ss.DurationInHours ?? 0,
                CreatedAt = ss.CreatedAt,
                ImageUrls = ss.Images?.Select(i => i.ImageUrl).ToList() ?? new List<string>()
            };

            ExpertProfileDto expertProfileDto = null;
            if (ss.ExpertProfile != null)
            {
                var userDto = ss.ExpertProfile.User != null ? new UserDto
                {
                    Name = ss.ExpertProfile.User.Name,
                    Email = ss.ExpertProfile.User.Email
                } : null;

                var reviews = ss.ExpertProfile.User?.ReviewsReceived?.Select(r => new ReviewDto
                {
                    Id = r.Id,
                    Score = r.Score,
                    Description = r.Description ?? "",
                    CreatedAt = r.CreatedAt
                }).ToList() ?? new List<ReviewDto>();

                expertProfileDto = new ExpertProfileDto
                {
                    Id = ss.ExpertProfile.Id,
                    ProfilePictureUrl = ss.ExpertProfile.ProfilePictureUrl,
                    Description = ss.ExpertProfile.Description,
                    CreatedAt = ss.ExpertProfile.CreatedAt,
                    User = userDto,
                    Reviews = reviews
                };
            }

            searchService.Expert = expertProfileDto;

            return searchService;
        }
    }
}