using Microsoft.EntityFrameworkCore;
using Google.Cloud.Storage.V1;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels.newApi.DataLayer.Models.PostGresModels;
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

        public async Task<IEnumerable<SearchServiceDetailDto>> GetAllServices()
        {
            var services = await _context.SearchServices
                .Include(ss => ss.Images)
                .Include(ss => ss.ExpertProfile)
                    .ThenInclude(ep => ep.User)
                .Include(ss => ss.Category)
                .Select(ss => MapToDetailDto(ss))
                .ToListAsync();

            return services;
        }

        public async Task<IEnumerable<SearchServiceResponseDto>> GetExpertServices(int expertId)
        {
            
            var services = await _context.SearchServices
                .Where(ss => ss.ExpertProfile.Id == expertId)
                .Include(ss => ss.Images)
                .Include(ss => ss.ExpertProfile)
                    .ThenInclude(ep => ep.User)
                .Include(ss => ss.Category)
                .Select(ss => MapToResponseDto(ss))
                .ToListAsync();


            return services;
        }

        public async Task<SearchServiceDetailDto> GetServiceById(int id)
        {
            var service = await _context.SearchServices
                .Include(ss => ss.Images)
                .Include(ss => ss.ExpertProfile)
                    .ThenInclude(ep => ep.User)
                .Include(ss => ss.Category)
                .FirstOrDefaultAsync(ss => ss.Id == id);

            return service == null ? null : MapToDetailDto(service);
        }

        public async Task<(bool success, SearchService service, List<string> imageUrls)> CreateSearchService(
            int userId,
            CreateSearchServiceRequestDto request)
        {
            var expertProfile = await _context.ExpertProfiles
                .FirstOrDefaultAsync(ep => ep.Id == request.ExpertProfileId && ep.UserId == userId);

            if (expertProfile == null)
                return (false, null, null);

            var category = await _context.Categories.FindAsync(request.CategoryId);
            if (category == null)
                return (false, null, null);

            var searchService = new SearchService
            {
                ExpertProfileId = request.ExpertProfileId,
                CategoryId = request.CategoryId,
                Price = request.Price,
                Conditions = request.Conditions,
                DurationInHours = request.DurationInHours,
                CreatedAt = DateTime.UtcNow
            };

            _context.SearchServices.Add(searchService);
            await _context.SaveChangesAsync();

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

        private static SearchServiceDetailDto MapToDetailDto(SearchService ss)
        {
            if (ss == null) return null;

            var searchService = new SearchServiceDetailDto
            {
                Id = ss.Id,
                CategoryId = ss.CategoryId,
                CategoryName = ss.Category?.Name ?? "Unknown Category",
                Price = ss.Price,
                Conditions = ss.Conditions,
                DurationInHours = ss.DurationInHours,
                CreatedAt = ss.CreatedAt,
                ImageUrls = ss.Images?.Select(i => i.ImageUrl).ToList() ?? new List<string>(),
                Expert = ss.ExpertProfile == null ? null : new ExpertProfileDto
                {
                    Id = ss.ExpertProfile.Id,
                    ProfilePictureUrl = ss.ExpertProfile.ProfilePictureUrl,
                    Description = ss.ExpertProfile.Description,
                    CreatedAt = ss.ExpertProfile.CreatedAt,
                    User = ss.ExpertProfile.User == null ? null : new UserDto
                    {
                        Name = ss.ExpertProfile.User.Name,
                        Email = ss.ExpertProfile.User.Email
                    }
                },
                CompletedSearches = ss.ExpertProfile?.User?.SearchHiresAsExpert?.Count(sh => sh.Status == "Completed") ?? 0,
                AverageRating = ss.ExpertProfile?.User?.ReviewsReceived != null && ss.ExpertProfile.User.ReviewsReceived.Any()
                    ? ss.ExpertProfile.User.ReviewsReceived.Average(r => r.Score)
                    : 0
            };

            return searchService;
        }


        private static SearchServiceResponseDto MapToResponseDto(SearchService ss)
        {
            var searchserviceresponse = new SearchServiceResponseDto
            {
                Id = ss.Id,
                CategoryId = ss.CategoryId,
                Price = ss.Price,
                Conditions = ss.Conditions,
                DurationInHours = ss.DurationInHours,
                CreatedAt = ss.CreatedAt,
                ImageUrls = ss.Images.Select(i => i.ImageUrl).ToList(),
                Expert = new ExpertProfileDto
                {
                    Id = ss.ExpertProfile.Id,
                    ProfilePictureUrl = ss.ExpertProfile.ProfilePictureUrl,
                    Description = ss.ExpertProfile.Description,
                    CreatedAt = ss.ExpertProfile.CreatedAt,
                    User = new UserDto
                    {
                        Name = ss.ExpertProfile.User.Name,
                        Email = ss.ExpertProfile.User.Email
                    }
                }
            };


            return searchserviceresponse;
        }
    }
}