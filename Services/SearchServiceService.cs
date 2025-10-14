using Microsoft.EntityFrameworkCore;
using Google.Cloud.Storage.V1;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using newApi.DataLayer;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using System.Globalization;

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

                // Parsear parámetros de entrada
                _logger.LogInformation("Parsing Latitude: {LatitudeRaw}, Longitude: {LongitudeRaw}", latitude, longitude);
                if (!decimal.TryParse(latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var searchLatitude))
                {
                    _logger.LogError("Failed to parse Latitude: {LatitudeRaw}", latitude);
                    throw new ArgumentException($"Invalid latitude format: {latitude}");
                }
                if (!decimal.TryParse(longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var searchLongitude))
                {
                    _logger.LogError("Failed to parse Longitude: {LongitudeRaw}", longitude);
                    throw new ArgumentException($"Invalid longitude format: {longitude}");
                }
                _logger.LogInformation("Parsed Latitude: {Latitude}, Parsed Longitude: {Longitude}", searchLatitude, searchLongitude);

                // Validación de coordenadas de entrada
                if (searchLatitude < -90m || searchLatitude > 90m)
                {
                    _logger.LogWarning("Latitude {Latitude} is out of valid range (-90 to 90)", searchLatitude);
                    throw new ArgumentException("Search coordinates must be within valid ranges (-90 to 90 for latitude, -180 to 180 for longitude)");
                }
                if (searchLongitude < -180m || searchLongitude > 180m)
                {
                    _logger.LogWarning("Longitude {Longitude} is out of valid range (-180 to 180)", searchLongitude);
                    throw new ArgumentException("Search coordinates must be within valid ranges (-90 to 90 for latitude, -180 to 180 for longitude)");
                }

                var query = _context.SearchServices
                    .Where(ss => ss.CategoryId == categoryId && ss.ServiceTypeId == serviceTypeId && ss.IsActive && !ss.ExpertProfile.IsOnVacation)
                    .Include(ss => ss.Images)
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                            .ThenInclude(r => r.Reviewer) // ✅ NUEVO: Incluir información del revisor
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                            .ThenInclude(r => r.ImagesCollection) // ✅ NUEVO: Incluir imágenes de las reviews
                    .Include(ss => ss.Category)
                    .Include(ss => ss.ServiceType)
                    .Include(ss => ss.SelectedDeliverableTypes)
                        .ThenInclude(ssdt => ssdt.DeliverableType);

                var services = await query.ToListAsync();

                _logger.LogInformation("Services before null coordinate filter: {ServiceCount}", services.Count);
                services = services
                    .Where(ss => !string.IsNullOrEmpty(ss.ExpertProfile?.Latitude) && !string.IsNullOrEmpty(ss.ExpertProfile?.Longitude))
                    .ToList();
                _logger.LogInformation("Services after null coordinate filter: {ServiceCount}", services.Count);

                var filteredServices = services
                    .Where(ss =>
                    {
                        // Parsear coordenadas de ExpertProfile
                        if (!decimal.TryParse(ss.ExpertProfile.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLat))
                        {
                            _logger.LogWarning("Invalid Latitude for Service ID {ServiceId}: {Latitude}", ss.Id, ss.ExpertProfile.Latitude);
                            return false;
                        }
                        if (!decimal.TryParse(ss.ExpertProfile.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLon))
                        {
                            _logger.LogWarning("Invalid Longitude for Service ID {ServiceId}: {Longitude}", ss.Id, ss.ExpertProfile.Longitude);
                            return false;
                        }

                        // Validar rangos de coordenadas
                        if (expertLat < -90m || expertLat > 90m || expertLon < -180m || expertLon > 180m)
                        {
                            _logger.LogWarning("Coordinates out of range for Service ID {ServiceId}: Latitude={Latitude}, Longitude={Longitude}", ss.Id, expertLat, expertLon);
                            return false;
                        }

                        var distance = CalculateDistance(searchLatitude, searchLongitude, expertLat, expertLon);
                        var isExtremeDistance = distance > 10000m;
                        _logger.LogInformation("Service ID {ServiceId} at coordinates ({ExpertLat}, {ExpertLon}) is at distance {Distance} km, Extreme: {IsExtreme}",
                            ss.Id, expertLat, expertLon, distance, isExtremeDistance);
                        if (isExtremeDistance)
                        {
                            _logger.LogWarning("Service ID {ServiceId} has an extreme distance ({Distance} km) which may indicate invalid coordinates", ss.Id, distance);
                        }
                        return distance <= locationRange || (isExtremeDistance && distance <= locationRange * 2);
                    })
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
        public static decimal CalculateDistance(decimal lat1, decimal lon1, decimal lat2, decimal lon2)
        {
            const double R = 6371; // Earth's radius in km
            var dLat = (double)(lat2 - lat1) * Math.PI / 180;
            var dLon = (double)(lon2 - lon1) * Math.PI / 180;
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos((double)lat1 * Math.PI / 180) * Math.Cos((double)lat2 * Math.PI / 180) *
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
                    .Where(ss => ss.ExpertProfileId == expertId && ss.IsActive);

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
                    .Include(ss => ss.ServiceType)
                    .Include(ss => ss.SelectedDeliverableTypes)
                        .ThenInclude(ssdt => ssdt.DeliverableType);

                var services = await query.ToListAsync();
                
                
                var mappedServices = services.Select(ss => MapToResponseDto(ss)).ToList();

                _logger.LogInformation("Retrieved {ServiceCount} expert services for ExpertId: {ExpertId}, ServiceTypeId: {ServiceTypeId}", mappedServices.Count, expertId, serviceTypeId);
                
                return mappedServices;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving expert services for ExpertId: {ExpertId}, ServiceTypeId: {ServiceTypeId}", expertId, serviceTypeId);
                throw;
            }
        }

//NUEVO PUSH
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
                            .ThenInclude(r => r.Reviewer) // ✅ NUEVO: Incluir información del revisor
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                            .ThenInclude(r => r.ImagesCollection) // ✅ NUEVO: Incluir imágenes de las reviews
                    .Include(ss => ss.Category)
                    .Include(ss => ss.ServiceType)
                        .ThenInclude(st => st.ServiceTypeCategory)
                    .Include(ss => ss.SelectedDeliverableTypes)
                        .ThenInclude(ssdt => ssdt.DeliverableType)
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



        public async Task<SearchServiceDetailDto> GetServiceByHireId(int id)
        {
            try
            {
                _logger.LogInformation("Fetching service by HireId: {Id}", id);

                // Retrieve the SearchService associated with the HireId, including related data
                var service = await _context.SearchServices
                    .Include(ss => ss.Images)
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                    .Include(ss => ss.Category)
                    .Include(ss => ss.ServiceType)
                        .ThenInclude(st => st.ServiceTypeCategory)
                    .Include(ss => ss.SelectedDeliverableTypes)
                        .ThenInclude(ssdt => ssdt.DeliverableType)
                    .FirstOrDefaultAsync(ss => _context.SearchHires.Any(sh => sh.Id == id && sh.SearchServiceId == ss.Id));

                if (service == null)
                {
                    _logger.LogWarning("Service not found for HireId: {Id}", id);
                    return null;
                }

                _logger.LogInformation("Successfully retrieved service with Id: {ServiceId} for HireId: {Id}", service.Id, id);
                return MapToDetailDto(service);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving service for HireId: {Id}", id);
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
                _logger.LogInformation("SelectedDeliverableTypes received: {SelectedDeliverableTypes}", request.SelectedDeliverableTypes);

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
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true
                };

                _context.SearchServices.Add(searchService);
                _logger.LogInformation("Attempting to save SearchService with ServiceTypeId: {ServiceTypeId}", searchService.ServiceTypeId);
                await _context.SaveChangesAsync();
                _logger.LogInformation("Successfully saved SearchService with Id: {ServiceId}", searchService.Id);

                // Procesar tipos de entregables seleccionados
                _logger.LogInformation("SelectedDeliverableTypes received: '{SelectedDeliverableTypes}'", request.SelectedDeliverableTypes);
                if (!string.IsNullOrEmpty(request.SelectedDeliverableTypes))
                {
                    try
                    {
                        var deliverableTypeIds = System.Text.Json.JsonSerializer.Deserialize<int[]>(request.SelectedDeliverableTypes);
                        _logger.LogInformation("Processing {Count} deliverable types for SearchService {ServiceId}: {DeliverableTypeIds}", 
                            deliverableTypeIds.Length, searchService.Id, string.Join(",", deliverableTypeIds));
                        
                        foreach (var deliverableTypeId in deliverableTypeIds)
                        {
                            var deliverableType = await _context.DeliverableTypes.FindAsync(deliverableTypeId);
                            if (deliverableType != null)
                            {
                                var searchServiceDeliverableType = new SearchServiceDeliverableType
                                {
                                    SearchServiceId = searchService.Id,
                                    DeliverableTypeId = deliverableTypeId,
                                    IsSelected = true,
                                    CreatedAt = DateTime.UtcNow,
                                    UpdatedAt = DateTime.UtcNow
                                };
                                _context.SearchServiceDeliverableTypes.Add(searchServiceDeliverableType);
                                _logger.LogInformation("Added deliverable type {DeliverableTypeId} ({Name}) to SearchService {ServiceId}", 
                                    deliverableTypeId, deliverableType.Name, searchService.Id);
                            }
                            else
                            {
                                _logger.LogWarning("DeliverableType {DeliverableTypeId} not found", deliverableTypeId);
                            }
                        }
                        
                        await _context.SaveChangesAsync();
                        _logger.LogInformation("Successfully saved deliverable types for SearchService {ServiceId}", searchService.Id);
                        
                        // Verificar que se guardaron correctamente
                        var savedDeliverableTypes = await _context.SearchServiceDeliverableTypes
                            .Where(ssdt => ssdt.SearchServiceId == searchService.Id)
                            .Include(ssdt => ssdt.DeliverableType)
                            .ToListAsync();
                        _logger.LogInformation("Verification: Found {Count} saved deliverable types for SearchService {ServiceId}: {DeliverableTypes}", 
                            savedDeliverableTypes.Count, searchService.Id, 
                            string.Join(",", savedDeliverableTypes.Select(sdt => $"{sdt.DeliverableType.Name}({sdt.DeliverableTypeId})")));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing deliverable types for SearchService {ServiceId}: {SelectedDeliverableTypes}", 
                            searchService.Id, request.SelectedDeliverableTypes);
                    }
                }
                else
                {
                    _logger.LogInformation("No deliverable types selected for SearchService {ServiceId}", searchService.Id);
                }

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
                                    "image/jpeg",
                                    outputStream
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
                ServiceTypeCategoryId = ss.ServiceType?.ServiceTypeCategoryId,
                RequiresAppointment = ss.ServiceType?.RequiresAppointment ?? false,
                Price = ss.Price,
                Conditions = ss.Conditions,
                DurationInHours = ss.DurationInHours ?? 0,
                CreatedAt = ss.CreatedAt,
                IsActive = ss.IsActive,
                ImageUrls = ss.Images?.Select(i => i.ImageUrl).ToList() ?? new List<string>(),
                SelectedDeliverableTypes = ss.SelectedDeliverableTypes?
                    .Select(ssdt => new DeliverableTypeDto
                    {
                        Id = ssdt.DeliverableType.Id,
                        Name = ssdt.DeliverableType.Name,
                        DisplayName = ssdt.DeliverableType.DisplayName,
                        Description = ssdt.DeliverableType.Description,
                        IsRequired = ssdt.DeliverableType.IsRequired,
                        IsActive = ssdt.DeliverableType.IsActive,
                        SortOrder = ssdt.DeliverableType.SortOrder
                    })
                    .ToList() ?? new List<DeliverableTypeDto>()
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
                    CreatedAt = r.CreatedAt,
                    Reviewer = r.Reviewer != null ? new UserDto
                    {
                        Id = r.Reviewer.Id,
                        Name = r.Reviewer.Name,
                        Email = r.Reviewer.Email,
                        ProfilePictureUrl = null // User no tiene ProfilePictureUrl, está en ExpertProfile
                    } : null,
                    ImageUrls = r.ImagesCollection?.Select(img => img.ImageUrl).ToList() ?? new List<string>()
                }).ToList() ?? new List<ReviewDto>();

                expertProfileDto = new ExpertProfileDto
                {
                    Id = ss.ExpertProfile.Id,
                    ProfilePictureUrl = ss.ExpertProfile.ProfilePictureUrl,
                    Description = ss.ExpertProfile.Description,
                    CreatedAt = ss.ExpertProfile.CreatedAt,
                    User = userDto,
                    Reviews = reviews,
                    Latitude = ss.ExpertProfile.Latitude,
                    Longitude = ss.ExpertProfile.Longitude,
                    IsOnVacation = ss.ExpertProfile.IsOnVacation
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
                SelectedDeliverableTypes = baseDto.SelectedDeliverableTypes,
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
                ServiceTypeCategoryId = ss.ServiceType?.ServiceTypeCategoryId,
                RequiresAppointment = ss.ServiceType?.RequiresAppointment ?? false,
                Price = ss.Price,
                Conditions = ss.Conditions,
                DurationInHours = ss.DurationInHours ?? 0,
                CreatedAt = ss.CreatedAt,
                IsActive = ss.IsActive,
                ImageUrls = ss.Images?.Select(i => i.ImageUrl).ToList() ?? new List<string>(),
                SelectedDeliverableTypes = ss.SelectedDeliverableTypes?
                    .Select(ssdt => new DeliverableTypeDto
                    {
                        Id = ssdt.DeliverableType.Id,
                        Name = ssdt.DeliverableType.Name,
                        DisplayName = ssdt.DeliverableType.DisplayName,
                        Description = ssdt.DeliverableType.Description,
                        IsRequired = ssdt.DeliverableType.IsRequired,
                        IsActive = ssdt.DeliverableType.IsActive,
                        SortOrder = ssdt.DeliverableType.SortOrder
                    })
                    .ToList() ?? new List<DeliverableTypeDto>()
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
                    CreatedAt = r.CreatedAt,
                    Reviewer = r.Reviewer != null ? new UserDto
                    {
                        Id = r.Reviewer.Id,
                        Name = r.Reviewer.Name,
                        Email = r.Reviewer.Email,
                        ProfilePictureUrl = null // User no tiene ProfilePictureUrl, está en ExpertProfile
                    } : null,
                    ImageUrls = r.ImagesCollection?.Select(img => img.ImageUrl).ToList() ?? new List<string>()
                }).ToList() ?? new List<ReviewDto>();

                expertProfileDto = new ExpertProfileDto
                {
                    Id = ss.ExpertProfile.Id,
                    ProfilePictureUrl = ss.ExpertProfile.ProfilePictureUrl,
                    Description = ss.ExpertProfile.Description,
                    CreatedAt = ss.ExpertProfile.CreatedAt,
                    User = userDto,
                    Reviews = reviews,
                    Latitude = ss.ExpertProfile.Latitude,
                    Longitude = ss.ExpertProfile.Longitude,
                    IsOnVacation = ss.ExpertProfile.IsOnVacation
                };
            }

            searchService.Expert = expertProfileDto;

            return searchService;
        }

        public async Task<(bool Success, SearchService NewService, List<string> ImageUrls)> UpdateSearchService(
            int userId,
            UpdateSearchServiceRequestDto request)
        {
            try
            {
                _logger.LogInformation("Updating SearchService with Id: {ServiceId} for User: {UserId}", request.ServiceId, userId);

                // Verificar que el servicio existe y pertenece al usuario
                var existingService = await _context.SearchServices
                    .Include(ss => ss.ExpertProfile)
                    .FirstOrDefaultAsync(ss => ss.Id == request.ServiceId && ss.ExpertProfile.UserId == userId);

                if (existingService == null)
                {
                    _logger.LogError("SearchService with Id: {ServiceId} not found or does not belong to User: {UserId}", request.ServiceId, userId);
                    return (false, null, null);
                }

                // Verificar que el servicio está activo
                if (!existingService.IsActive)
                {
                    _logger.LogError("Cannot update inactive SearchService with Id: {ServiceId}", request.ServiceId);
                    return (false, null, null);
                }

                // Validar los datos de la actualización
                var serviceTypeExists = await _context.ServiceTypes.AnyAsync(st => st.Id == request.ServiceTypeId);
                if (!serviceTypeExists)
                {
                    _logger.LogError("Invalid ServiceTypeId: {ServiceTypeId}", request.ServiceTypeId);
                    return (false, null, null);
                }

                var category = await _context.Categories.FindAsync(request.CategoryId);
                if (category == null)
                {
                    _logger.LogError("Invalid CategoryId: {CategoryId}", request.CategoryId);
                    return (false, null, null);
                }

                // Paso 1: Inactivar el servicio existente
                existingService.IsActive = false;
                _logger.LogInformation("Deactivating existing SearchService with Id: {ServiceId}", existingService.Id);

                // Paso 2: Crear el nuevo servicio con los datos actualizados
                var newSearchService = new SearchService
                {
                    ExpertProfileId = existingService.ExpertProfileId, // Mantener el mismo ExpertProfile
                    CategoryId = request.CategoryId,
                    ServiceTypeId = request.ServiceTypeId,
                    Price = request.Price,
                    Conditions = request.Conditions,
                    DurationInHours = request.DurationInHours ?? 0,
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true
                };

                _context.SearchServices.Add(newSearchService);
                _logger.LogInformation("Creating new SearchService with updated data for ServiceTypeId: {ServiceTypeId}", newSearchService.ServiceTypeId);
                
                await _context.SaveChangesAsync();
                _logger.LogInformation("Successfully created new SearchService with Id: {NewServiceId} and deactivated old service with Id: {OldServiceId}", 
                    newSearchService.Id, existingService.Id);

                // Paso 3: Procesar tipos de entregables seleccionados
                _logger.LogInformation("SelectedDeliverableTypes received: '{SelectedDeliverableTypes}'", request.SelectedDeliverableTypes);
                if (!string.IsNullOrEmpty(request.SelectedDeliverableTypes))
                {
                    try
                    {
                        var deliverableTypeIds = System.Text.Json.JsonSerializer.Deserialize<int[]>(request.SelectedDeliverableTypes);
                        _logger.LogInformation("Processing {Count} deliverable types for updated SearchService {ServiceId}: {DeliverableTypeIds}", 
                            deliverableTypeIds.Length, newSearchService.Id, string.Join(",", deliverableTypeIds));
                        
                        foreach (var deliverableTypeId in deliverableTypeIds)
                        {
                            var deliverableType = await _context.DeliverableTypes.FindAsync(deliverableTypeId);
                            if (deliverableType != null)
                            {
                                var searchServiceDeliverableType = new SearchServiceDeliverableType
                                {
                                    SearchServiceId = newSearchService.Id,
                                    DeliverableTypeId = deliverableTypeId,
                                    IsSelected = true,
                                    CreatedAt = DateTime.UtcNow,
                                    UpdatedAt = DateTime.UtcNow
                                };
                                _context.SearchServiceDeliverableTypes.Add(searchServiceDeliverableType);
                                _logger.LogInformation("Added deliverable type {DeliverableTypeId} ({Name}) to updated SearchService {ServiceId}", 
                                    deliverableTypeId, deliverableType.Name, newSearchService.Id);
                            }
                            else
                            {
                                _logger.LogWarning("DeliverableType {DeliverableTypeId} not found", deliverableTypeId);
                            }
                        }
                        
                        await _context.SaveChangesAsync();
                        _logger.LogInformation("Successfully saved deliverable types for updated SearchService {ServiceId}", newSearchService.Id);
                        
                        // Verificar que se guardaron correctamente
                        var savedDeliverableTypes = await _context.SearchServiceDeliverableTypes
                            .Where(ssdt => ssdt.SearchServiceId == newSearchService.Id)
                            .Include(ssdt => ssdt.DeliverableType)
                            .ToListAsync();
                        _logger.LogInformation("Verification: Found {Count} saved deliverable types for SearchService {ServiceId}: {DeliverableTypes}", 
                            savedDeliverableTypes.Count, newSearchService.Id, 
                            string.Join(",", savedDeliverableTypes.Select(sdt => $"{sdt.DeliverableType.Name}({sdt.DeliverableTypeId})")));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing deliverable types for updated SearchService {ServiceId}: {SelectedDeliverableTypes}", 
                            newSearchService.Id, request.SelectedDeliverableTypes);
                    }
                }
                else
                {
                    _logger.LogInformation("No deliverable types selected for updated SearchService {ServiceId}", newSearchService.Id);
                }

                // Paso 4: Procesar las imágenes si se proporcionaron
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
                                    "image/jpeg",
                                    outputStream
                                );
                            }
                        }

                        var imageUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";
                        imageUrls.Add(imageUrl);

                        var searchServiceImage = new SearchServiceImage
                        {
                            SearchServiceId = newSearchService.Id,
                            ImageUrl = imageUrl,
                            ImageObjectName = objectName,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.SearchServiceImages.Add(searchServiceImage);
                    }
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Successfully processed {ImageCount} images for new SearchService with Id: {ServiceId}", 
                        imageUrls.Count, newSearchService.Id);
                }

                return (true, newSearchService, imageUrls);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating SearchService with Id: {ServiceId} for User: {UserId}", request.ServiceId, userId);
                throw;
            }
        }

        public async Task<bool> DeleteSearchService(int serviceId, int userId)
        {
            try
            {
                _logger.LogInformation("Attempting to delete SearchService with Id: {ServiceId} by User: {UserId}", serviceId, userId);

                // Buscar el servicio y verificar que pertenezca al usuario
                var searchService = await _context.SearchServices
                    .Include(ss => ss.ExpertProfile)
                    .FirstOrDefaultAsync(ss => ss.Id == serviceId && ss.ExpertProfile.UserId == userId);

                if (searchService == null)
                {
                    _logger.LogWarning("SearchService with Id: {ServiceId} not found or does not belong to User: {UserId}", serviceId, userId);
                    return false;
                }

                // Verificar si el servicio ya está inactivo
                if (!searchService.IsActive)
                {
                    _logger.LogInformation("SearchService with Id: {ServiceId} is already inactive", serviceId);
                    return true; // Ya está "eliminado"
                }

                // Marcar como inactivo (soft delete)
                searchService.IsActive = false;
                await _context.SaveChangesAsync();

                _logger.LogInformation("Successfully deactivated SearchService with Id: {ServiceId}", serviceId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting SearchService with Id: {ServiceId} by User: {UserId}", serviceId, userId);
                return false;
            }
        }
    }
}