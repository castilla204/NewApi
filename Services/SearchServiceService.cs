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

        public async Task<IEnumerable<SearchServiceDetailDto>> GetAllServices(int categoryId, int serviceTypeId)
        {
            try
            {
                _logger.LogInformation("Fetching services for CategoryId: {CategoryId}, ServiceTypeId: {ServiceTypeId}", categoryId, serviceTypeId);
                IQueryable<SearchService> query = _context.SearchServices
                    .Where(ss => ss.CategoryId == categoryId && ss.ServiceTypeId == serviceTypeId);

                query = query
                    .Include(ss => ss.Images)
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                    .Include(ss => ss.Category)
                    .Include(ss => ss.ServiceType);

                var services = await query
                    .Select(ss => MapToDetailDto(ss))
                    .ToListAsync();

                _logger.LogInformation("Retrieved {ServiceCount} services for CategoryId: {CategoryId}, ServiceTypeId: {ServiceTypeId}", services.Count, categoryId, serviceTypeId);
                return services;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving services with CategoryId: {CategoryId}, ServiceTypeId: {ServiceTypeId}", categoryId, serviceTypeId);
                throw;
            }
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
                    DurationInHours = request.DurationInHours,
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
                                    bucket: bucketName,
                                    objectName: objectName,
                                    contentType: "image/jpeg",
                                    source: outputStream
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
                DurationInHours = ss.DurationInHours,
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

                var reviews = ss.ExpertProfile.User?.ReviewsReceived?.Select(r => new ReviewDto // Using DTOs.ReviewDto
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
                DurationInHours = ss.DurationInHours,
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

                var reviews = ss.ExpertProfile.User?.ReviewsReceived?.Select(r => new ReviewDto // Using DTOs.ReviewDto
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