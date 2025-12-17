using Microsoft.EntityFrameworkCore;
using Google.Cloud.Storage.V1;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using newApi.DataLayer;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.enums;
using System.Globalization;

namespace newApi.Services
{
    public class SearchServiceService : ISearchServiceService
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly StorageClient _storageClient;
        private readonly ISignedUrlService _signedUrlService;

        public SearchServiceService(
            AppDbContext context,
            IConfiguration configuration,
            StorageClient storageClient,
            ISignedUrlService signedUrlService)
        {
            _context = context;
            _configuration = configuration;
            _storageClient = storageClient;
            _signedUrlService = signedUrlService;
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
                if (categoryId <= 0 || serviceTypeId <= 0)
                {
                    throw new ArgumentException("CategoryId and ServiceTypeId must be greater than 0");
                }

                if (string.IsNullOrEmpty(latitude) || string.IsNullOrEmpty(longitude) || locationRange <= 0)
                {
                    throw new ArgumentException("Latitude, Longitude, and LocationRange are required and must be valid");
                }

                // Parsear parámetros de entrada
                if (!decimal.TryParse(latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var searchLatitude))
                {
                    throw new ArgumentException($"Invalid latitude format: {latitude}");
                }
                if (!decimal.TryParse(longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var searchLongitude))
                {
                    throw new ArgumentException($"Invalid longitude format: {longitude}");
                }
                // Validación de coordenadas de entrada
                if (searchLatitude < -90m || searchLatitude > 90m)
                {
                    throw new ArgumentException("Search coordinates must be within valid ranges (-90 to 90 for latitude, -180 to 180 for longitude)");
                }
                if (searchLongitude < -180m || searchLongitude > 180m)
                {
                    throw new ArgumentException("Search coordinates must be within valid ranges (-90 to 90 for latitude, -180 to 180 for longitude)");
                }

                var query = _context.SearchServices
                    .AsNoTracking() // ✅ CORRECCIÓN: Forzar consulta desde BD, evitar tracking de EF Core
                    .Where(ss => ss.CategoryId == categoryId && ss.ServiceTypeId == serviceTypeId && ss.IsActive && !ss.ExpertProfile.IsOnVacation
                        && (ss.ExpertProfile.StripeStatus == StripeStatus.Approved && ss.ExpertProfile.OnboardingCompleted
                            || ss.ExpertProfile.StripeStatus == StripeStatus.PendingVerification)) // ✅ FIX: Permitir PendingVerification
                    .Include(ss => ss.Images)
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                            .ThenInclude(r => r.Reviewer) // ✅ NUEVO: Incluir información del revisor
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                            .ThenInclude(r => r.ImagesCollection) // ✅ NUEVO: Incluir imágenes de las reviews
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                            .ThenInclude(r => r.SearchHire) // ✅ INTERNACIONALIZACIÓN: Cargar SearchHire para obtener ExpertCountry
                    .Include(ss => ss.Category)
                    .Include(ss => ss.ServiceType)
                    .Include(ss => ss.SelectedDeliverableTypes)
                        .ThenInclude(ssdt => ssdt.DeliverableType);

                var services = await query.ToListAsync();
                services = services
                    .Where(ss => ss.IsActive && !string.IsNullOrEmpty(ss.ExpertProfile?.Latitude) && !string.IsNullOrEmpty(ss.ExpertProfile?.Longitude)) // ✅ CORRECCIÓN: Filtrar explícitamente por IsActive después de cargar
                    .ToList();
                
                // ✅ NUEVO: Cargar todas las disponibilidades activas de los expertos en una sola consulta
                var expertProfileIds = services.Select(ss => ss.ExpertProfileId).Distinct().ToList();
                var availabilities = await _context.ExpertAvailabilities
                    .Where(ea => expertProfileIds.Contains(ea.ExpertId) && ea.IsActive && ea.EffectiveTo == null)
                    .OrderByDescending(ea => ea.EffectiveFrom)
                    .ToListAsync();
                
                // Agrupar por ExpertId y tomar la más reciente (si hay duplicados)
                var availabilityByExpert = availabilities
                    .GroupBy(ea => ea.ExpertId)
                    .ToDictionary(g => g.Key, g => g.First());

                var filteredServices = services
                    .Where(ss =>
                    {
                        // Parsear coordenadas de ExpertProfile
                        if (!decimal.TryParse(ss.ExpertProfile.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLat))
                        {
                            return false;
                        }
                        if (!decimal.TryParse(ss.ExpertProfile.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLon))
                        {
                            return false;
                        }

                        // Validar rangos de coordenadas
                        if (expertLat < -90m || expertLat > 90m || expertLon < -180m || expertLon > 180m)
                        {
                            return false;
                        }

                        var distance = CalculateDistance(searchLatitude, searchLongitude, expertLat, expertLon);
                        var isExtremeDistance = distance > 10000m;
                        return distance <= locationRange || (isExtremeDistance && distance <= locationRange * 2);
                    })
                    .Select(ss => MapToDetailDto(ss, availabilityByExpert))
                    .ToList();
                
                return filteredServices;
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        public async Task<ExpertMapResponseDto> GetMapExperts(
            int categoryId, 
            int serviceTypeId,
            decimal? northeastLat = null,
            decimal? northeastLng = null,
            decimal? southwestLat = null,
            decimal? southwestLng = null,
            int? zoom = null,
            int limit = 100)
        {
            try
            {
                if (categoryId <= 0 || serviceTypeId <= 0)
                {
                    throw new ArgumentException("CategoryId and ServiceTypeId must be greater than 0");
                }

                // ✅ Validar bounds si se proporcionan
                bool hasBounds = northeastLat.HasValue && northeastLng.HasValue && 
                                southwestLat.HasValue && southwestLng.HasValue;
                
                if (hasBounds)
                {
                    // Validar que northeast > southwest
                    if (northeastLat.Value <= southwestLat.Value || northeastLng.Value <= southwestLng.Value)
                    {
                        throw new ArgumentException("Invalid bounds: northeast must be greater than southwest");
                    }
                    
                    // Validar rangos de coordenadas
                    if (northeastLat.Value < -90m || northeastLat.Value > 90m ||
                        southwestLat.Value < -90m || southwestLat.Value > 90m ||
                        northeastLng.Value < -180m || northeastLng.Value > 180m ||
                        southwestLng.Value < -180m || southwestLng.Value > 180m)
                    {
                        throw new ArgumentException("Invalid coordinate ranges. Latitude must be between -90 and 90, Longitude between -180 and 180");
                    }
                }

                // ✅ Determinar límite según zoom
                int maxResults = limit;
                if (zoom.HasValue)
                {
                    maxResults = zoom.Value switch
                    {
                        >= 15 => Math.Min(limit, 200),  // Zoom alto: más servicios
                        >= 12 => Math.Min(limit, 100),  // Zoom medio
                        _ => Math.Min(limit, 50)        // Zoom bajo: menos servicios
                    };
                }

                var query = _context.SearchServices
                    .AsNoTracking() // ✅ CORRECCIÓN: Forzar consulta desde BD, evitar tracking de EF Core
                    .Where(ss => ss.CategoryId == categoryId && ss.ServiceTypeId == serviceTypeId && ss.IsActive && !ss.ExpertProfile.IsOnVacation
                        && (ss.ExpertProfile.StripeStatus == StripeStatus.Approved && ss.ExpertProfile.OnboardingCompleted
                            || ss.ExpertProfile.StripeStatus == StripeStatus.PendingVerification)) // ✅ FIX: Permitir PendingVerification
                    .Where(ss => !string.IsNullOrEmpty(ss.ExpertProfile.Latitude) && !string.IsNullOrEmpty(ss.ExpertProfile.Longitude)); // ✅ MEJORA: Filtrar coordenadas vacías en SQL

                // ✅ OPTIMIZACIÓN: Aplicar límite temprano cuando hay bounds para reducir datos cargados
                // Aunque el filtrado final sea en memoria, limitamos la cantidad de datos desde SQL
                if (hasBounds)
                {
                    // Aplicar un límite generoso antes de cargar (para reducir memoria)
                    // El límite real se aplica después del filtrado por bounds
                    query = query.Take(maxResults * 3); // Cargar 3x el límite para tener margen después del filtrado
                }

                query = query
                    .Include(ss => ss.Images)
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                    .Include(ss => ss.Category)
                    .Include(ss => ss.ServiceType);

                var services = await query.ToListAsync();

                // ✅ OPTIMIZACIÓN: Filtrar por bounds en memoria (necesario porque coordenadas son strings)
                // Nota: Aunque no podemos filtrar directamente en SQL con CAST fácilmente en EF Core,
                // al menos limitamos la cantidad de datos cargados antes del filtrado
                if (hasBounds)
                {
                    services = services.Where(ss =>
                    {
                        if (!decimal.TryParse(ss.ExpertProfile.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLat) ||
                            !decimal.TryParse(ss.ExpertProfile.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLng))
                        {
                            return false;
                        }

                        // Verificar que el experto esté dentro de los bounds
                        // Nota: Para longitudes que cruzan el meridiano 180/-180, se necesita lógica adicional
                        bool latInBounds = expertLat >= southwestLat.Value && expertLat <= northeastLat.Value;
                        bool lngInBounds = expertLng >= southwestLng.Value && expertLng <= northeastLng.Value;
                        
                        return latInBounds && lngInBounds;
                    }).ToList();

                    // ✅ OPTIMIZACIÓN: Ordenar por distancia al centro del bounds
                    var centerLat = (northeastLat.Value + southwestLat.Value) / 2;
                    var centerLng = (northeastLng.Value + southwestLng.Value) / 2;
                    
                    services = services.OrderBy(ss =>
                    {
                        if (!decimal.TryParse(ss.ExpertProfile.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLat) ||
                            !decimal.TryParse(ss.ExpertProfile.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLng))
                        {
                            return decimal.MaxValue; // Poner al final si no se puede parsear
                        }
                        return CalculateDistance(centerLat, centerLng, expertLat, expertLng);
                    }).ToList();

                    // ✅ OPTIMIZACIÓN: Aplicar límite después del filtrado y ordenamiento
                    if (services.Count > maxResults)
                    {
                        services = services.Take(maxResults).ToList();
                    }
                }

                // ✅ NUEVO: Cargar todas las disponibilidades activas de los expertos en una sola consulta
                var expertProfileIds = services.Select(ss => ss.ExpertProfileId).Distinct().ToList();
                var availabilities = await _context.ExpertAvailabilities
                    .Where(ea => expertProfileIds.Contains(ea.ExpertId) && ea.IsActive && ea.EffectiveTo == null)
                    .OrderByDescending(ea => ea.EffectiveFrom)
                    .ToListAsync();

                // Agrupar por ExpertId y tomar la más reciente (si hay duplicados)
                var availabilityByExpert = availabilities
                    .GroupBy(ea => ea.ExpertId)
                    .ToDictionary(g => g.Key, g => g.First());

                // Agrupar servicios por experto para evitar duplicados
                var expertGroups = services.GroupBy(ss => ss.ExpertProfile.User.Id);

                var expertMapDtos = expertGroups.Select(expertGroup =>
                {
                    var firstService = expertGroup.First();
                    var expert = firstService.ExpertProfile.User;
                    
                    // Obtener disponibilidad
                    CurrentExpertAvailabilityDto? availabilityDto = null;
                    if (availabilityByExpert.TryGetValue(firstService.ExpertProfile.Id, out var currentAvailability))
                    {
                        var daysOfWeek = System.Text.Json.JsonSerializer.Deserialize<List<string>>(currentAvailability.DaysOfWeek) ?? new List<string>();
                        availabilityDto = new CurrentExpertAvailabilityDto
                        {
                            Id = currentAvailability.Id,
                            DaysOfWeek = daysOfWeek,
                            StartTime = currentAvailability.StartTime,
                            EndTime = currentAvailability.EndTime,
                            EffectiveFrom = currentAvailability.EffectiveFrom
                        };
                    }

                    return new ExpertMapDto
                    {
                        Id = expert.Id,
                        Name = expert.Name,
                        ProfilePictureUrl = ResolveProfilePictureUrl(firstService.ExpertProfile),
                        AverageRating = expert.ReviewsReceived != null && expert.ReviewsReceived.Any()
                            ? expert.ReviewsReceived.Average(r => r.Score)
                            : 0,
                        TotalReviews = expert.ReviewsReceived?.Count ?? 0,
                        CompletedSearches = expert.SearchHiresAsExpert?.Count(sh => sh.Status.StatusValue == "completed") ?? 0,
                        RegisteredSince = firstService.ExpertProfile.CreatedAt,
                        Latitude = firstService.ExpertProfile.Latitude,
                        Longitude = firstService.ExpertProfile.Longitude,
                        // ✅ NUEVO: Precio del servicio
                        Price = firstService.Price,
                        // ✅ NUEVO: Datos adicionales solicitados (descripciones, tipos y horarios)
                        ServiceDescription = firstService.Conditions,
                        ServiceTypeName = firstService.ServiceType?.Name ?? "Unknown",
                        ServiceTypeDescription = firstService.ServiceType?.Description ?? string.Empty,
                        CurrentAvailability = availabilityDto
                    };
                }).ToList();

                var response = new ExpertMapResponseDto
                {
                    Experts = expertMapDtos,
                    TotalCount = expertMapDtos.Count
                };
                return response;
            }
            catch (Exception ex)
            {
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

        public async Task<(IEnumerable<SearchServiceResponseDto> services, int totalCount)> GetExpertServices(int expertId, int? serviceTypeId = null, int page = 1, int pageSize = 20)
        {
            try
            {
                // Validar parámetros
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                IQueryable<SearchService> query = _context.SearchServices
                    .AsNoTracking() // ✅ CORRECCIÓN: Forzar consulta desde BD, evitar tracking de EF Core
                    .Where(ss => ss.ExpertProfileId == expertId && ss.IsActive);

                if (serviceTypeId.HasValue)
                {
                    query = query.Where(ss => ss.ServiceTypeId == serviceTypeId.Value);
                }

                var totalCount = await query.CountAsync();

                query = query
                    .Include(ss => ss.Images)
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                            .ThenInclude(r => r.SearchHire) // ✅ INTERNACIONALIZACIÓN: Cargar SearchHire para obtener ExpertCountry
                    .Include(ss => ss.Category)
                    .Include(ss => ss.ServiceType)
                    .Include(ss => ss.SelectedDeliverableTypes)
                        .ThenInclude(ssdt => ssdt.DeliverableType)
                    .OrderByDescending(ss => ss.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize);

                var services = await query.ToListAsync();
                
                var mappedServices = services.Select(ss => MapToResponseDto(ss)).ToList();
                return (mappedServices, totalCount);
            }
            catch (Exception ex)
            {
                throw;
            }
        }

//NUEVO PUSH
        public async Task<SearchServiceDetailDto> GetServiceById(int id)
        {
            try
            {
                var service = await _context.SearchServices
                    .AsNoTracking() // ✅ CORRECCIÓN: Forzar consulta desde BD, evitar tracking de EF Core
                    .Include(ss => ss.Images)
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                            .ThenInclude(r => r.Reviewer) // ✅ NUEVO: Incluir información del revisor
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                            .ThenInclude(r => r.ImagesCollection) // ✅ NUEVO: Incluir imágenes de las reviews
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                            .ThenInclude(r => r.SearchHire) // ✅ INTERNACIONALIZACIÓN: Cargar SearchHire para obtener ExpertCountry
                    .Include(ss => ss.Category)
                    .Include(ss => ss.ServiceType)
                        .ThenInclude(st => st.ServiceTypeCategory)
                    .Include(ss => ss.SelectedDeliverableTypes)
                        .ThenInclude(ssdt => ssdt.DeliverableType)
                    .FirstOrDefaultAsync(ss => ss.Id == id);

                if (service == null)
                {
                    return null;
                }

                // ✅ NUEVO: Cargar disponibilidad del experto
                Dictionary<int, ExpertAvailability>? availabilityByExpert = null;
                if (service.ExpertProfile != null)
                {
                    var currentAvailability = await _context.ExpertAvailabilities
                        .Where(ea => ea.ExpertId == service.ExpertProfile.Id && ea.IsActive && ea.EffectiveTo == null)
                        .OrderByDescending(ea => ea.EffectiveFrom)
                        .FirstOrDefaultAsync();
                    
                    if (currentAvailability != null)
                    {
                        availabilityByExpert = new Dictionary<int, ExpertAvailability>
                        {
                            { service.ExpertProfile.Id, currentAvailability }
                        };
                    }
                }

                return MapToDetailDto(service, availabilityByExpert);
            }
            catch (Exception ex)
            {
                throw;
            }
        }


        public async Task<SearchServiceDetailDto> GetServiceByHireId(int id)
        {
            try
            {
                // Retrieve the SearchService associated with the HireId, including related data
                var service = await _context.SearchServices
                    .AsNoTracking() // ✅ CORRECCIÓN: Forzar consulta desde BD, evitar tracking de EF Core
                    .Include(ss => ss.Images)
                    .Include(ss => ss.ExpertProfile)
                        .ThenInclude(ep => ep.User)
                        .ThenInclude(u => u.ReviewsReceived)
                            .ThenInclude(r => r.SearchHire) // ✅ INTERNACIONALIZACIÓN: Cargar SearchHire para obtener ExpertCountry
                    .Include(ss => ss.Category)
                    .Include(ss => ss.ServiceType)
                        .ThenInclude(st => st.ServiceTypeCategory)
                    .Include(ss => ss.SelectedDeliverableTypes)
                        .ThenInclude(ssdt => ssdt.DeliverableType)
                    .FirstOrDefaultAsync(ss => _context.SearchHires.Any(sh => sh.Id == id && sh.SearchServiceId == ss.Id));

                if (service == null)
                {
                    return null;
                }

                // ✅ NUEVO: Cargar disponibilidad del experto
                Dictionary<int, ExpertAvailability>? availabilityByExpert = null;
                if (service.ExpertProfile != null)
                {
                    var currentAvailability = await _context.ExpertAvailabilities
                        .Where(ea => ea.ExpertId == service.ExpertProfile.Id && ea.IsActive && ea.EffectiveTo == null)
                        .OrderByDescending(ea => ea.EffectiveFrom)
                        .FirstOrDefaultAsync();
                    
                    if (currentAvailability != null)
                    {
                        availabilityByExpert = new Dictionary<int, ExpertAvailability>
                        {
                            { service.ExpertProfile.Id, currentAvailability }
                        };
                    }
                }
                return MapToDetailDto(service, availabilityByExpert);
            }
            catch (Exception ex)
            {
                throw;
            }
        }


        public async Task<(bool Success, SearchService Service, List<string> ImageUrls)> CreateSearchService(
            int userId,
            CreateSearchServiceRequestDto request)
        {
            try
            {
                var serviceTypeExists = await _context.ServiceTypes.AnyAsync(st => st.Id == request.ServiceTypeId);
                if (!serviceTypeExists)
                {
                    return (false, null, null);
                }

                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.Id == request.ExpertProfileId && ep.UserId == userId);
                if (expertProfile == null)
                {
                    return (false, null, null);
                }

                var category = await _context.Categories
                    .Include(c => c.Parent)
                    .FirstOrDefaultAsync(c => c.Id == request.CategoryId);
                if (category == null)
                {
                    return (false, null, null);
                }

                // ✅ VALIDACIÓN: Determinar la categoría padre
                // Si la categoría seleccionada es una subcategoría (tiene ParentId), usar el ParentId
                // Si es una categoría padre (ParentId es null), usar su propio Id
                int parentCategoryId = category.ParentId ?? category.Id;

                // ✅ VALIDACIÓN: Verificar que el experto no tenga ya un servicio activo con la misma categoría PADRE Y el mismo tipo de servicio
                // Permite múltiples servicios de la misma categoría padre si el ServiceTypeId es diferente
                // Pero no permite dos servicios con la misma categoría padre Y el mismo ServiceTypeId
                var existingServices = await _context.SearchServices
                    .Where(ss => ss.ExpertProfileId == request.ExpertProfileId 
                            && ss.ServiceTypeId == request.ServiceTypeId
                            && ss.IsActive == true)
                    .Include(ss => ss.Category)
                    .ToListAsync();

                // Verificar si algún servicio existente tiene la misma categoría padre
                var existingServiceWithSameParentCategoryAndType = existingServices
                    .Where(ss =>
                    {
                        // Determinar la categoría padre del servicio existente
                        var existingCategory = ss.Category;
                        int existingParentCategoryId = existingCategory?.ParentId ?? existingCategory?.Id ?? 0;
                        return existingParentCategoryId == parentCategoryId;
                    })
                    .FirstOrDefault();

                if (existingServiceWithSameParentCategoryAndType != null)
                {
                    var existingCategoryName = existingServiceWithSameParentCategoryAndType.Category?.Name ?? "desconocida";
                    var parentCategoryName = category.Parent?.Name ?? category.Name;
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
                await _context.SaveChangesAsync();
                // Procesar tipos de entregables seleccionados
                if (!string.IsNullOrEmpty(request.SelectedDeliverableTypes))
                {
                    try
                    {
                        var deliverableTypeIds = System.Text.Json.JsonSerializer.Deserialize<int[]>(request.SelectedDeliverableTypes);
                        
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
                            }
                            else
                            {
                            }
                        }
                        
                        await _context.SaveChangesAsync();
                        // Verificar que se guardaron correctamente
                        var savedDeliverableTypes = await _context.SearchServiceDeliverableTypes
                            .Where(ssdt => ssdt.SearchServiceId == searchService.Id)
                            .Include(ssdt => ssdt.DeliverableType)
                            .ToListAsync();
                    }
                    catch (Exception ex)
                    {
                    }
                }
                else
                {
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
                                // ✅ FIX: Quitar PredefinedAcl cuando el bucket tiene uniform bucket-level access habilitado
                                // El acceso se controla mediante IAM policies del bucket, no ACLs por objeto
                                await _storageClient.UploadObjectAsync(
                                    bucketName,
                                    objectName,
                                    "image/jpeg",
                                    outputStream
                                    // ✅ REMOVIDO: PredefinedAcl no es compatible con uniform bucket-level access
                                    // options: new UploadObjectOptions { PredefinedAcl = PredefinedObjectAcl.Private }
                                );
                            }
                        }

                        var imageUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";
                        var searchServiceImage = new SearchServiceImage
                        {
                            SearchServiceId = searchService.Id,
                            ImageUrl = imageUrl,
                            ImageObjectName = objectName,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.SearchServiceImages.Add(searchServiceImage);
                        imageUrls.Add(ResolveServiceImageUrl(searchServiceImage));
                    }
                    await _context.SaveChangesAsync();
                }

                return (true, searchService, imageUrls);
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        private SearchServiceDetailDto MapToDetailDto(SearchService ss, Dictionary<int, ExpertAvailability>? availabilityByExpert = null)
        {
            if (ss == null) return null;

            var baseDto = new SearchServiceResponseDto
            {
                Id = ss.Id,
                CategoryId = ss.CategoryId,
                ServiceTypeId = ss.ServiceTypeId,
                ServiceTypeName = ss.ServiceType?.Name ?? "Unknown Service Type",
                ServiceTypeDescription = ss.ServiceType?.Description ?? string.Empty, // ✅ NUEVO
                ServiceTypeCategoryId = ss.ServiceType?.ServiceTypeCategoryId,
                RequiresAppointment = ss.ServiceType?.RequiresAppointment ?? false,
                Price = ss.Price,
                Conditions = ss.Conditions,
                DurationInHours = ss.DurationInHours ?? 0,
                CreatedAt = ss.CreatedAt,
                IsActive = ss.IsActive,
                ImageUrls = ss.Images?
                    .Select(img => ResolveServiceImageUrl(img))
                    .Where(url => !string.IsNullOrEmpty(url))
                    .ToList() ?? new List<string>(),
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
                    ImageUrls = r.ImagesCollection?
                        .Select(img => ResolveReviewImageUrl(img))
                        .Where(url => !string.IsNullOrEmpty(url))
                        .ToList() ?? new List<string>(),
                    // ✅ INTERNACIONALIZACIÓN: País donde se realizó la contratación
                    Country = r.SearchHire?.ExpertCountry
                }).ToList() ?? new List<ReviewDto>();

                // ✅ NUEVO: Obtener la disponibilidad actual activa del experto
                CurrentExpertAvailabilityDto? availabilityDto = null;
                if (availabilityByExpert != null && availabilityByExpert.TryGetValue(ss.ExpertProfile.Id, out var currentAvailability))
                {
                    var daysOfWeek = System.Text.Json.JsonSerializer.Deserialize<List<string>>(currentAvailability.DaysOfWeek) ?? new List<string>();
                    availabilityDto = new CurrentExpertAvailabilityDto
                    {
                        Id = currentAvailability.Id,
                        DaysOfWeek = daysOfWeek,
                        StartTime = currentAvailability.StartTime,
                        EndTime = currentAvailability.EndTime,
                        EffectiveFrom = currentAvailability.EffectiveFrom
                    };
                }

                expertProfileDto = new ExpertProfileDto
                {
                    Id = ss.ExpertProfile.Id,
                    ProfilePictureUrl = ResolveProfilePictureUrl(ss.ExpertProfile),
                    Description = ss.ExpertProfile.Description,
                    StripeAccountId = ss.ExpertProfile.StripeAccountId,
                    CreatedAt = ss.ExpertProfile.CreatedAt,
                    User = userDto,
                    Reviews = reviews,
                    Latitude = ss.ExpertProfile.Latitude,
                    Longitude = ss.ExpertProfile.Longitude,
                    StripeStatus = ss.ExpertProfile.StripeStatus, // ✅ CORRECCIÓN: Mapear StripeStatus
                    StripeStatusDetails = ss.ExpertProfile.StripeStatusDetails, // ✅ CORRECCIÓN: Mapear StripeStatusDetails
                    OnboardingCompleted = ss.ExpertProfile.OnboardingCompleted, // ✅ CORRECCIÓN: Mapear OnboardingCompleted
                    IsOnVacation = ss.ExpertProfile.IsOnVacation,
                    CurrentAvailability = availabilityDto, // ✅ NUEVO: Incluir horarios de disponibilidad
                    // ✅ FUTURE REQUIREMENTS
                    StripeFutureRequirements = ss.ExpertProfile.StripeFutureRequirements,
                    StripeFutureDueAt = ss.ExpertProfile.StripeFutureDueAt,
                    // ✅ INTERNACIONALIZACIÓN: Timezone y país del experto
                    Timezone = ss.ExpertProfile.Timezone,
                    Country = ss.ExpertProfile.Country
                };
            }

            baseDto.Expert = expertProfileDto;

            var detailDto = new SearchServiceDetailDto
            {
                Id = baseDto.Id,
                CategoryId = baseDto.CategoryId,
                ServiceTypeId = baseDto.ServiceTypeId,
                ServiceTypeName = baseDto.ServiceTypeName,
                ServiceTypeDescription = baseDto.ServiceTypeDescription, // ✅ FIXED: Copiar descripción
                ServiceTypeCategoryId = baseDto.ServiceTypeCategoryId, // ✅ CORRECCIÓN: Incluir ServiceTypeCategoryId
                RequiresAppointment = baseDto.RequiresAppointment, // ✅ CORRECCIÓN: Incluir RequiresAppointment
                Price = baseDto.Price,
                Conditions = baseDto.Conditions,
                DurationInHours = baseDto.DurationInHours,
                CreatedAt = baseDto.CreatedAt,
                IsActive = baseDto.IsActive, // ✅ CORRECCIÓN: Incluir IsActive
                ImageUrls = baseDto.ImageUrls,
                Expert = baseDto.Expert,
                SelectedDeliverableTypes = baseDto.SelectedDeliverableTypes,
                CategoryName = ss.Category?.Name ?? "Unknown Category",
                CompletedSearches = ss.ExpertProfile?.User?.SearchHiresAsExpert?.Count(sh => sh.Status.StatusValue == "completed") ?? 0,
                AverageRating = ss.ExpertProfile?.User?.ReviewsReceived != null && ss.ExpertProfile.User.ReviewsReceived.Any()
                    ? ss.ExpertProfile.User.ReviewsReceived.Average(r => r.Score)
                    : 0
            };

            return detailDto;
        }

        private SearchServiceResponseDto MapToResponseDto(SearchService ss)
        {
            
            var searchService = new SearchServiceResponseDto
            {
                Id = ss.Id,
                CategoryId = ss.CategoryId,
                ServiceTypeId = ss.ServiceTypeId,
                ServiceTypeName = ss.ServiceType?.Name ?? "Unknown Service Type",
                ServiceTypeDescription = ss.ServiceType?.Description ?? string.Empty, // ✅ NUEVO
                ServiceTypeCategoryId = ss.ServiceType?.ServiceTypeCategoryId,
                RequiresAppointment = ss.ServiceType?.RequiresAppointment ?? false,
                Price = ss.Price,
                Conditions = ss.Conditions,
                DurationInHours = ss.DurationInHours ?? 0,
                CreatedAt = ss.CreatedAt,
                IsActive = ss.IsActive,
                ImageUrls = ss.Images?
                    .Select(img => ResolveServiceImageUrl(img))
                    .Where(url => !string.IsNullOrEmpty(url))
                    .ToList() ?? new List<string>(),
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
                    ImageUrls = r.ImagesCollection?
                        .Select(img => ResolveReviewImageUrl(img))
                        .Where(url => !string.IsNullOrEmpty(url))
                        .ToList() ?? new List<string>(),
                    // ✅ INTERNACIONALIZACIÓN: País donde se realizó la contratación
                    Country = r.SearchHire?.ExpertCountry
                }).ToList() ?? new List<ReviewDto>();

                expertProfileDto = new ExpertProfileDto
                {
                    Id = ss.ExpertProfile.Id,
                    ProfilePictureUrl = ResolveProfilePictureUrl(ss.ExpertProfile),
                    Description = ss.ExpertProfile.Description,
                    CreatedAt = ss.ExpertProfile.CreatedAt,
                    User = userDto,
                    Reviews = reviews,
                    Latitude = ss.ExpertProfile.Latitude,
                    Longitude = ss.ExpertProfile.Longitude,
                    IsOnVacation = ss.ExpertProfile.IsOnVacation,
                    // ✅ FUTURE REQUIREMENTS
                    StripeFutureRequirements = ss.ExpertProfile.StripeFutureRequirements,
                    StripeFutureDueAt = ss.ExpertProfile.StripeFutureDueAt,
                    // ✅ INTERNACIONALIZACIÓN: Timezone y país del experto
                    Timezone = ss.ExpertProfile.Timezone,
                    Country = ss.ExpertProfile.Country
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
                // Verificar que el servicio existe y pertenece al usuario
                var existingService = await _context.SearchServices
                    .Include(ss => ss.ExpertProfile)
                    .FirstOrDefaultAsync(ss => ss.Id == request.ServiceId && ss.ExpertProfile.UserId == userId);

                if (existingService == null)
                {
                    return (false, null, null);
                }

                // Verificar que el servicio está activo
                if (!existingService.IsActive)
                {
                    return (false, null, null);
                }

                // Validar los datos de la actualización
                var serviceTypeExists = await _context.ServiceTypes.AnyAsync(st => st.Id == request.ServiceTypeId);
                if (!serviceTypeExists)
                {
                    return (false, null, null);
                }

                var category = await _context.Categories
                    .Include(c => c.Parent)
                    .FirstOrDefaultAsync(c => c.Id == request.CategoryId);
                if (category == null)
                {
                    return (false, null, null);
                }

                // ✅ VALIDACIÓN: Determinar la categoría padre
                // Si la categoría seleccionada es una subcategoría (tiene ParentId), usar el ParentId
                // Si es una categoría padre (ParentId es null), usar su propio Id
                int parentCategoryId = category.ParentId ?? category.Id;

                // ✅ VALIDACIÓN: Verificar que el experto no tenga ya otro servicio activo con la misma categoría PADRE Y el mismo tipo de servicio
                // (excluyendo el servicio que se está actualizando)
                // Permite múltiples servicios de la misma categoría padre si el ServiceTypeId es diferente
                // Pero no permite dos servicios con la misma categoría padre Y el mismo ServiceTypeId
                var existingServices = await _context.SearchServices
                    .Where(ss => ss.ExpertProfileId == existingService.ExpertProfileId 
                            && ss.ServiceTypeId == request.ServiceTypeId
                            && ss.IsActive == true
                            && ss.Id != request.ServiceId) // Excluir el servicio que se está actualizando
                    .Include(ss => ss.Category)
                    .ToListAsync();

                // Verificar si algún servicio existente tiene la misma categoría padre
                var existingServiceWithSameParentCategoryAndType = existingServices
                    .Where(ss =>
                    {
                        // Determinar la categoría padre del servicio existente
                        var existingCategory = ss.Category;
                        if (existingCategory == null) return false;
                        int existingParentCategoryId = existingCategory.ParentId ?? existingCategory.Id;
                        return existingParentCategoryId == parentCategoryId;
                    })
                    .FirstOrDefault();

                if (existingServiceWithSameParentCategoryAndType != null)
                {
                    var existingCategoryName = existingServiceWithSameParentCategoryAndType.Category?.Name ?? "desconocida";
                    var parentCategoryName = category.Parent?.Name ?? category.Name;
                    return (false, null, null);
                }

                // Paso 1: Inactivar el servicio existente
                existingService.IsActive = false;
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
                await _context.SaveChangesAsync();
                // Paso 3: Procesar tipos de entregables seleccionados
                if (!string.IsNullOrEmpty(request.SelectedDeliverableTypes))
                {
                    try
                    {
                        var deliverableTypeIds = System.Text.Json.JsonSerializer.Deserialize<int[]>(request.SelectedDeliverableTypes);
                        
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
                            }
                            else
                            {
                            }
                        }
                        
                        await _context.SaveChangesAsync();
                        // Verificar que se guardaron correctamente
                        var savedDeliverableTypes = await _context.SearchServiceDeliverableTypes
                            .Where(ssdt => ssdt.SearchServiceId == newSearchService.Id)
                            .Include(ssdt => ssdt.DeliverableType)
                            .ToListAsync();
                    }
                    catch (Exception ex)
                    {
                    }
                }
                else
                {
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
                                // ✅ FIX: Quitar PredefinedAcl cuando el bucket tiene uniform bucket-level access habilitado
                                // El acceso se controla mediante IAM policies del bucket, no ACLs por objeto
                                await _storageClient.UploadObjectAsync(
                                    bucketName,
                                    objectName,
                                    "image/jpeg",
                                    outputStream
                                    // ✅ REMOVIDO: PredefinedAcl no es compatible con uniform bucket-level access
                                    // options: new UploadObjectOptions { PredefinedAcl = PredefinedObjectAcl.Private }
                                );
                            }
                        }

                        var imageUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";
                        var searchServiceImage = new SearchServiceImage
                        {
                            SearchServiceId = newSearchService.Id,
                            ImageUrl = imageUrl,
                            ImageObjectName = objectName,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.SearchServiceImages.Add(searchServiceImage);
                        imageUrls.Add(ResolveServiceImageUrl(searchServiceImage));
                    }
                    await _context.SaveChangesAsync();
                }

                return (true, newSearchService, imageUrls);
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        public async Task<bool> DeleteSearchService(int serviceId, int userId)
        {
            try
            {
                // Buscar el servicio y verificar que pertenezca al usuario
                var searchService = await _context.SearchServices
                    .Include(ss => ss.ExpertProfile)
                    .FirstOrDefaultAsync(ss => ss.Id == serviceId && ss.ExpertProfile.UserId == userId);

                if (searchService == null)
                {
                    return false;
                }

                // Verificar si el servicio ya está inactivo
                if (!searchService.IsActive)
                {
                    return true; // Ya está "eliminado"
                }

                // Marcar como inactivo (soft delete)
                searchService.IsActive = false;
                await _context.SaveChangesAsync();
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        private string ResolveProfilePictureUrl(ExpertProfile? expertProfile)
        {
            if (expertProfile == null)
            {
                return "/default-avatar.png";
            }

            var fallback = string.IsNullOrWhiteSpace(expertProfile.ProfilePictureUrl)
                ? "/default-avatar.png"
                : expertProfile.ProfilePictureUrl;

            return _signedUrlService.GetSignedUrl(expertProfile.ProfilePictureObjectName ?? string.Empty) ?? fallback;
        }

        private string ResolveServiceImageUrl(SearchServiceImage? image)
        {
            if (image == null)
            {
                return string.Empty;
            }

            var fallback = string.IsNullOrWhiteSpace(image.ImageUrl) ? string.Empty : image.ImageUrl;
            return _signedUrlService.GetSignedUrl(image.ImageObjectName ?? string.Empty) ?? fallback;
        }

        private string ResolveReviewImageUrl(ReviewImage? image)
        {
            if (image == null)
            {
                return string.Empty;
            }

            var fallback = string.IsNullOrWhiteSpace(image.ImageUrl) ? string.Empty : image.ImageUrl;
            return _signedUrlService.GetSignedUrl(image.ImageObjectName ?? string.Empty) ?? fallback;
        }
    }
}