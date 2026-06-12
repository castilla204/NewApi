using Microsoft.EntityFrameworkCore;
using Google.Cloud.Storage.V1;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using newApi.DataLayer;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.enums;
using System.Globalization;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace newApi.Services
{
    public class SearchServiceService : ISearchServiceService
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly StorageClient _storageClient;
        private readonly ISignedUrlService _signedUrlService;
        private readonly ISupabaseStorageService _supabaseStorage;
        private readonly ILogger<SearchServiceService> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public SearchServiceService(
            AppDbContext context,
            IConfiguration configuration,
            StorageClient storageClient,
            ISignedUrlService signedUrlService,
            ISupabaseStorageService supabaseStorage,
            ILogger<SearchServiceService> logger,
            IServiceScopeFactory serviceScopeFactory)
        {
            _context = context;
            _configuration = configuration;
            _storageClient = storageClient;
            _signedUrlService = signedUrlService;
            _supabaseStorage = supabaseStorage;
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;
        }

        public async Task<(IEnumerable<SearchServiceDetailDto> services, int totalCount)> GetAllServices(
            int categoryId,
            int serviceTypeId,
            string latitude,
            string longitude,
            int locationRange,
            int page = 1,
            int pageSize = 50,
            CancellationToken cancellationToken = default)
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
                            || ss.ExpertProfile.StripeStatus == StripeStatus.PendingVerification)
                            // 🧩 PERFIL COMPLETO (Stripe-first): invisible hasta rellenar foto, descripción y ubicación.
                            && ss.ExpertProfile.Description != null && ss.ExpertProfile.Description != ""
                            && ss.ExpertProfile.ProfilePictureUrl != null && ss.ExpertProfile.ProfilePictureUrl != ""
                            // 📱 OBLIGATORIO: móvil verificado (no fijo) para ser visible — los avisos van por SMS.
                            && ss.ExpertProfile.User.PhoneVerified
                            && (ss.ExpertProfile.User.PhoneLineType == null || ss.ExpertProfile.User.PhoneLineType != "landline")
                            && ss.ExpertProfile.Latitude != null && ss.ExpertProfile.Latitude != "") // ✅ FIX: Permitir PendingVerification
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

                var services = await query.ToListAsync(cancellationToken);
                services = services
                    .Where(ss => ss.IsActive && !string.IsNullOrEmpty(ss.ExpertProfile?.Latitude) && !string.IsNullOrEmpty(ss.ExpertProfile?.Longitude)) // ✅ CORRECCIÓN: Filtrar explícitamente por IsActive después de cargar
                    .ToList();
                
                // ✅ NUEVO: Cargar todas las disponibilidades activas de los expertos en una sola consulta
                var expertProfileIds = services.Select(ss => ss.ExpertProfileId).Distinct().ToList();
                var availabilities = await _context.ExpertAvailabilities
                    .Where(ea => expertProfileIds.Contains(ea.ExpertId) && ea.IsActive && ea.EffectiveTo == null)
                    .OrderByDescending(ea => ea.EffectiveFrom)
                    .ToListAsync(cancellationToken);
                
                // Agrupar por ExpertId y tomar la más reciente (si hay duplicados)
                var availabilityByExpert = availabilities
                    .GroupBy(ea => ea.ExpertId)
                    .ToDictionary(g => g.Key, g => g.First());

                // ✅ Validar parámetros de paginación
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 100) pageSize = 50; // Máximo 100 por página
                
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
                        // ✅ Filtrar por distancia: dentro del rango del cliente Y del radio de
                        // trabajo del experto (WorkRadiusKm == 0 = solo en su taller, el cliente
                        // se desplaza → no se excluye por distancia).
                        return distance <= locationRange
                            && (ss.ExpertProfile.WorkRadiusKm == 0 || distance <= ss.ExpertProfile.WorkRadiusKm);
                    })
                    .OrderBy(ss =>
                    {
                        // Ordenar por distancia para devolver los más cercanos primero
                        if (!decimal.TryParse(ss.ExpertProfile.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLat) ||
                            !decimal.TryParse(ss.ExpertProfile.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLon))
                        {
                            return decimal.MaxValue;
                        }
                        return CalculateDistance(searchLatitude, searchLongitude, expertLat, expertLon);
                    })
                    .ToList();
                
                // ✅ Total count antes de paginación
                var totalCount = filteredServices.Count;
                
                // ✅ Si no hay servicios en el rango, devolver los más cercanos disponibles (sin límite de distancia)
                // Esto asegura que siempre haya servicios si existen en la base de datos
                if (totalCount == 0)
                {
                    var allServicesWithDistance = services
                        .Select(ss =>
                        {
                            if (!decimal.TryParse(ss.ExpertProfile.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLat) ||
                                !decimal.TryParse(ss.ExpertProfile.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLon))
                            {
                                return null;
                            }
                            var distance = CalculateDistance(searchLatitude, searchLongitude, expertLat, expertLon);
                            return new { Service = ss, Distance = distance };
                        })
                        .Where(x => x != null)
                        .OrderBy(x => x!.Distance)
                        .ToList();
                    
                    totalCount = allServicesWithDistance.Count;
                    
                    // ✅ Aplicar paginación
                    var paginatedServices = allServicesWithDistance
                        .Skip((page - 1) * pageSize)
                        .Take(pageSize)
                        .Select(x => MapToDetailDto(x!.Service, availabilityByExpert))
                        .ToList();
                    
                    return (paginatedServices, totalCount);
                }
                
                // ✅ Aplicar paginación a servicios filtrados
                var paginatedFilteredServices = filteredServices
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(ss => MapToDetailDto(ss, availabilityByExpert))
                    .ToList();
                
                return (paginatedFilteredServices, totalCount);
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
            int limit = 100,
            CancellationToken cancellationToken = default)
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
                            || ss.ExpertProfile.StripeStatus == StripeStatus.PendingVerification)
                            // 🧩 PERFIL COMPLETO (Stripe-first): invisible hasta rellenar foto, descripción y ubicación.
                            && ss.ExpertProfile.Description != null && ss.ExpertProfile.Description != ""
                            && ss.ExpertProfile.ProfilePictureUrl != null && ss.ExpertProfile.ProfilePictureUrl != ""
                            // 📱 OBLIGATORIO: móvil verificado (no fijo) para ser visible — los avisos van por SMS.
                            && ss.ExpertProfile.User.PhoneVerified
                            && (ss.ExpertProfile.User.PhoneLineType == null || ss.ExpertProfile.User.PhoneLineType != "landline")
                            && ss.ExpertProfile.Latitude != null && ss.ExpertProfile.Latitude != "") // ✅ FIX: Permitir PendingVerification
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

                var services = await query.ToListAsync(cancellationToken);

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
                    .ToListAsync(cancellationToken);

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
                        CompletedSearches = expert.SearchHiresAsExpert?.Count(sh => sh.Status != null && sh.Status.StatusValue == "completed") ?? 0,
                        RegisteredSince = firstService.ExpertProfile.CreatedAt,
                        Latitude = firstService.ExpertProfile.Latitude,
                        Longitude = firstService.ExpertProfile.Longitude,
                        WorkRadiusKm = firstService.ExpertProfile.WorkRadiusKm,
                        // ✅ NUEVO: Precio del servicio
                        Price = firstService.Price,
                        // Round 24: currency original del precio (ISO 4217). Default 'EUR' si null.
                        Currency = string.IsNullOrEmpty(firstService.Currency) ? "EUR" : firstService.Currency,
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

        /// <summary>
        /// Obtiene marcadores ultra ligeros para el mapa (solo coordenadas + precio)
        /// Optimizado para carga inicial rápida - 10-50x más rápido que GetMapExperts
        /// Similar a cómo Airbnb/Google Maps cargan marcadores iniciales
        /// </summary>
        public async Task<MapMarkersResponseDto> GetMapMarkers(
            int categoryId,
            int serviceTypeId,
            decimal? northeastLat = null,
            decimal? northeastLng = null,
            decimal? southwestLat = null,
            decimal? southwestLng = null,
            int? zoom = null,
            int limit = 500,
            CancellationToken cancellationToken = default)
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
                    if (northeastLat.Value <= southwestLat.Value || northeastLng.Value <= southwestLng.Value)
                    {
                        throw new ArgumentException("Invalid bounds: northeast must be greater than southwest");
                    }
                }

                // ✅ Determinar límite según zoom
                int maxResults = limit;
                if (zoom.HasValue)
                {
                    maxResults = zoom.Value switch
                    {
                        >= 15 => Math.Min(limit, 500),  // Zoom alto: más marcadores
                        >= 12 => Math.Min(limit, 200),  // Zoom medio
                        _ => Math.Min(limit, 100)        // Zoom bajo: menos marcadores
                    };
                }

                // ✅ OPTIMIZACIÓN CRÍTICA: Solo SELECT de campos mínimos (ultra rápido)
                var query = _context.SearchServices
                    .AsNoTracking()
                    .Where(ss => ss.CategoryId == categoryId
                        && ss.ServiceTypeId == serviceTypeId
                        && ss.IsActive
                        && !ss.ExpertProfile.IsOnVacation
                        && (ss.ExpertProfile.StripeStatus == StripeStatus.Approved && ss.ExpertProfile.OnboardingCompleted
                            || ss.ExpertProfile.StripeStatus == StripeStatus.PendingVerification)
                            // 🧩 PERFIL COMPLETO (Stripe-first): invisible hasta rellenar foto, descripción y ubicación.
                            && ss.ExpertProfile.Description != null && ss.ExpertProfile.Description != ""
                            && ss.ExpertProfile.ProfilePictureUrl != null && ss.ExpertProfile.ProfilePictureUrl != ""
                            // 📱 OBLIGATORIO: móvil verificado (no fijo) para ser visible — los avisos van por SMS.
                            && ss.ExpertProfile.User.PhoneVerified
                            && (ss.ExpertProfile.User.PhoneLineType == null || ss.ExpertProfile.User.PhoneLineType != "landline")
                            && ss.ExpertProfile.Latitude != null && ss.ExpertProfile.Latitude != "")
                    .Where(ss => !string.IsNullOrEmpty(ss.ExpertProfile.Latitude)
                        && !string.IsNullOrEmpty(ss.ExpertProfile.Longitude));

                // ✅ Filtrar por bounds directamente en SQL (muy rápido)
                if (hasBounds)
                {
                    // ✅ CORREGIDO: Usar SQL directo con parámetros para filtrar por bounds (más eficiente y seguro)
                    // Round 24: añadido ss.""Currency"" al SELECT para que MapMarkerDto contenga la
                    // moneda original del precio (UI lo necesita para convertir a la preferencia del usuario).
                    var sqlQuery = @"
                        SELECT ss.""Id"", ss.""Price"", ep.""Latitude"", ep.""Longitude"", COALESCE(ss.""Currency"", 'EUR') AS ""Currency""
                        FROM ""SearchServices"" ss
                        INNER JOIN ""ExpertProfiles"" ep ON ss.""ExpertProfileId"" = ep.""Id""
                        WHERE ss.""CategoryId"" = @categoryId
                          AND ss.""ServiceTypeId"" = @serviceTypeId
                          AND ss.""IsActive"" = true
                          AND ep.""IsOnVacation"" = false
                          -- 🛡️ T6 FIX: alinear con filtro LINQ línea ~89. Antes permitía:
                          --   StripeStatus=1 (Pending) AND OnboardingCompleted=true  ← raro pero estaba
                          --   StripeStatus=0 (NotRequested)  ← BUG: experto sin Stripe NO puede recibir transfers
                          --   StripeStatus=2 (Approved)
                          -- Ahora solo Approved+OnboardingCompleted O PendingVerification (6),
                          -- igual que el filtro LINQ de GetAllServices.
                          AND ((ep.""StripeStatus"" = 2 AND ep.""OnboardingCompleted"" = true) OR ep.""StripeStatus"" = 6)
                          AND ep.""Latitude"" IS NOT NULL
                          AND ep.""Latitude"" != ''
                          AND ep.""Longitude"" IS NOT NULL
                          AND ep.""Longitude"" != ''
                          AND CAST(ep.""Latitude"" AS NUMERIC) >= @southwestLat
                          AND CAST(ep.""Latitude"" AS NUMERIC) <= @northeastLat
                          AND CAST(ep.""Longitude"" AS NUMERIC) >= @southwestLng
                          AND CAST(ep.""Longitude"" AS NUMERIC) <= @northeastLng
                        LIMIT @maxResults";

                    var markers = new List<MapMarkerDto>();
                    using (var command = _context.Database.GetDbConnection().CreateCommand())
                    {
                        command.CommandText = sqlQuery;
                        
                        // ✅ Agregar parámetros con tipos correctos
                        var categoryIdParam = command.CreateParameter();
                        categoryIdParam.ParameterName = "@categoryId";
                        categoryIdParam.Value = categoryId;
                        categoryIdParam.DbType = System.Data.DbType.Int32;
                        command.Parameters.Add(categoryIdParam);
                        
                        var serviceTypeIdParam = command.CreateParameter();
                        serviceTypeIdParam.ParameterName = "@serviceTypeId";
                        serviceTypeIdParam.Value = serviceTypeId;
                        serviceTypeIdParam.DbType = System.Data.DbType.Int32;
                        command.Parameters.Add(serviceTypeIdParam);
                        
                        var southwestLatParam = command.CreateParameter();
                        southwestLatParam.ParameterName = "@southwestLat";
                        southwestLatParam.Value = southwestLat;
                        southwestLatParam.DbType = System.Data.DbType.Decimal;
                        command.Parameters.Add(southwestLatParam);
                        
                        var northeastLatParam = command.CreateParameter();
                        northeastLatParam.ParameterName = "@northeastLat";
                        northeastLatParam.Value = northeastLat;
                        northeastLatParam.DbType = System.Data.DbType.Decimal;
                        command.Parameters.Add(northeastLatParam);
                        
                        var southwestLngParam = command.CreateParameter();
                        southwestLngParam.ParameterName = "@southwestLng";
                        southwestLngParam.Value = southwestLng;
                        southwestLngParam.DbType = System.Data.DbType.Decimal;
                        command.Parameters.Add(southwestLngParam);
                        
                        var northeastLngParam = command.CreateParameter();
                        northeastLngParam.ParameterName = "@northeastLng";
                        northeastLngParam.Value = northeastLng;
                        northeastLngParam.DbType = System.Data.DbType.Decimal;
                        command.Parameters.Add(northeastLngParam);
                        
                        var maxResultsParam = command.CreateParameter();
                        maxResultsParam.ParameterName = "@maxResults";
                        maxResultsParam.Value = maxResults;
                        maxResultsParam.DbType = System.Data.DbType.Int32;
                        command.Parameters.Add(maxResultsParam);
                        _context.Database.OpenConnection();
                        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                        {
                            while (await reader.ReadAsync(cancellationToken))
                            {
                                markers.Add(new MapMarkerDto
                                {
                                    Id = reader.GetInt32(0),
                                    ServiceId = reader.GetInt32(0),
                                    Price = reader.GetDecimal(1),
                                    Latitude = reader.GetString(2),
                                    Longitude = reader.GetString(3),
                                    // Round 24: currency leído desde el SELECT (col índice 4)
                                    Currency = reader.IsDBNull(4) ? "EUR" : reader.GetString(4)
                                });
                            }
                        }
                        _context.Database.CloseConnection();
                    }

                    return new MapMarkersResponseDto
                    {
                        Markers = markers,
                        TotalCount = markers.Count
                    };
                }

                // ✅ Sin bounds: Cargar todos los marcadores (solo 4 campos)
                // Round 24: incluir Currency para conversión multi-moneda en frontend.
                var allMarkers = await query
                    .Select(ss => new MapMarkerDto
                    {
                        Id = ss.Id,
                        ServiceId = ss.Id,
                        Latitude = ss.ExpertProfile.Latitude,
                        Longitude = ss.ExpertProfile.Longitude,
                        Price = ss.Price,
                        Currency = string.IsNullOrEmpty(ss.Currency) ? "EUR" : ss.Currency
                    })
                    .Take(maxResults)
                    .ToListAsync(cancellationToken);

                return new MapMarkersResponseDto
                {
                    Markers = allMarkers,
                    TotalCount = allMarkers.Count
                };
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        /// <summary>
        /// Obtiene información completa para el sidebar (mínimo 3 imágenes + horario + descripción)
        /// Optimizado para mostrar cards con información suficiente sin cargar todo
        /// </summary>
        public async Task<MapSidebarResponseDto> GetMapSidebar(
            int[] serviceIds,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (serviceIds == null || serviceIds.Length == 0)
                {
                    return new MapSidebarResponseDto { Services = new List<MapSidebarServiceDto>() };
                }

                // ✅ OPTIMIZACIÓN: Cargar información necesaria + mínimo 3 imágenes
                var services = await _context.SearchServices
                    .AsNoTracking()
                    .Where(ss => serviceIds.Contains(ss.Id))
                    .Select(ss => new
                    {
                        Id = ss.Id,
                        Price = ss.Price,
                        // Round 24: currency original del precio para conversión en frontend.
                        Currency = ss.Currency,
                        ServiceDescription = ss.Conditions,  // ✅ Descripción del servicio
                        ServiceTypeName = ss.ServiceType.Name,
                        ExpertId = (int?)ss.ExpertProfileId,
                        ExpertName = ss.ExpertProfile.User.Name,
                        ExpertProfilePictureUrl = ss.ExpertProfile.ProfilePictureUrl,
                        ExpertProfilePictureObjectName = ss.ExpertProfile.ProfilePictureObjectName,
                        AverageRating = ss.ExpertProfile.User.ReviewsReceived.Any()
                            ? ss.ExpertProfile.User.ReviewsReceived.Average(r => (double)r.Score)
                            : 0.0,
                        TotalReviews = ss.ExpertProfile.User.ReviewsReceived.Count,
                        // ✅ Mínimo 3 imágenes (no solo la primera)
                        Images = ss.Images
                            .OrderBy(img => img.Id)
                            .Take(3)  // Mínimo 3 imágenes
                            .Select(img => new
                            {
                                ImageUrl = img.ImageUrl,
                                ImageObjectName = img.ImageObjectName
                            })
                            .ToList(),
                        Latitude = ss.ExpertProfile.Latitude,
                        Longitude = ss.ExpertProfile.Longitude,
                        WorkRadiusKm = ss.ExpertProfile.WorkRadiusKm
                    })
                    .ToListAsync(cancellationToken);

                // ✅ Cargar disponibilidades de los expertos en batch
                var expertProfileIds = services.Where(s => s.ExpertId.HasValue).Select(s => s.ExpertId!.Value).Distinct().ToList();
                var availabilities = await _context.ExpertAvailabilities
                    .Where(ea => expertProfileIds.Contains(ea.ExpertId) && ea.IsActive && ea.EffectiveTo == null)
                    .OrderByDescending(ea => ea.EffectiveFrom)
                    .ToListAsync(cancellationToken);

                var availabilityByExpert = availabilities
                    .GroupBy(ea => ea.ExpertId)
                    .ToDictionary(g => g.Key, g => g.First());

                // ✅ Procesar URLs firmadas en memoria (mínimo 3 imágenes)
                var processedServices = services.Select(s =>
                {
                    // Procesar imágenes (mínimo 3) usando la misma lógica que ResolveServiceImageUrl
                    var imageUrls = s.Images
                        .Select(img =>
                        {
                            // ✅ Si la URL es externa (no de Google Cloud Storage), devolverla directamente
                            if (!string.IsNullOrWhiteSpace(img.ImageUrl))
                            {
                                var bucketName = _configuration["GoogleCloud:BucketName"];
                                var isExternalUrl = string.IsNullOrWhiteSpace(bucketName) || 
                                                   !img.ImageUrl.Contains($"storage.googleapis.com/{bucketName}", StringComparison.OrdinalIgnoreCase);
                                
                                if (isExternalUrl)
                                {
                                    // URL externa (Unsplash, Pexels, etc.) - devolver directamente sin signed URL
                                    return img.ImageUrl;
                                }
                            }

                            // ✅ Si es URL de Google Cloud Storage o hay ImageObjectName, generar signed URL
                            var fallback = string.IsNullOrWhiteSpace(img.ImageUrl) ? string.Empty : img.ImageUrl;
                            return _signedUrlService.GetSignedUrl(img.ImageObjectName ?? string.Empty) ?? fallback;
                        })
                        .Where(url => !string.IsNullOrEmpty(url))
                        .ToList();

                    // Obtener disponibilidad del experto
                    CurrentExpertAvailabilityDto? availabilityDto = null;
                    if (s.ExpertId.HasValue && availabilityByExpert.TryGetValue(s.ExpertId.Value, out var currentAvailability))
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

                    return new MapSidebarServiceDto
                    {
                        Id = s.Id,
                        Price = s.Price,
                        // Round 24: poblar Currency en el DTO (default 'EUR' si null).
                        Currency = string.IsNullOrEmpty(s.Currency) ? "EUR" : s.Currency,
                        ServiceDescription = s.ServiceDescription ?? string.Empty,  // ✅ Descripción
                        ServiceTypeName = s.ServiceTypeName ?? string.Empty,
                        ExpertName = s.ExpertName ?? string.Empty,
                        ExpertProfilePictureUrl = !string.IsNullOrWhiteSpace(s.ExpertProfilePictureObjectName)
                            ? _signedUrlService.GetSignedUrl(s.ExpertProfilePictureObjectName) ?? s.ExpertProfilePictureUrl ?? string.Empty
                            : s.ExpertProfilePictureUrl ?? string.Empty,
                        AverageRating = s.AverageRating,
                        TotalReviews = s.TotalReviews,
                        ImageUrls = imageUrls,  // ✅ Mínimo 3 imágenes
                        Latitude = s.Latitude ?? string.Empty,
                        Longitude = s.Longitude ?? string.Empty,
                        WorkRadiusKm = s.WorkRadiusKm,
                        CurrentAvailability = availabilityDto  // ✅ Horario
                    };
                }).ToList();

                return new MapSidebarResponseDto
                {
                    Services = processedServices,
                    TotalCount = processedServices.Count
                };
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        /// <summary>
        /// Obtiene servicios con información completa filtrados por bounds del mapa (cuando el usuario se mueve por el mapa)
        /// 
        /// ✅ OPTIMIZACIONES IMPLEMENTADAS:
        /// - Límites inteligentes según zoom (menos datos en zoom bajo)
        /// - Filtrado por bounds antes de cargar toda la información
        /// - Ordenamiento por distancia al centro del mapa
        /// 
        /// 🚀 OPTIMIZACIONES FUTURAS RECOMENDADAS (ver MAP_PERFORMANCE_OPTIMIZATION_GUIDE.md):
        /// - Índices espaciales PostGIS (100-1000x más rápido)
        /// - Caché Redis para áreas visitadas frecuentemente
        /// - Compresión HTTP de respuestas
        /// </summary>
        public async Task<(IEnumerable<SearchServiceDetailDto> services, int totalCount)> GetMapExpertsWithDetails(
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
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (categoryId <= 0 || serviceTypeId <= 0)
                {
                    throw new ArgumentException("CategoryId and ServiceTypeId must be greater than 0");
                }

                // ✅ CORREGIDO: Validar bounds - permitir que northeastLng < southwestLng cuando cruza el meridiano
                // Esto puede ocurrir cuando el área visible cruza el meridiano 180/-180
                if (northeastLat <= southwestLat)
                {
                    throw new ArgumentException("Invalid bounds: northeastLat must be greater than southwestLat");
                }
                
                // ✅ Validar que el ancho de bounds no sea excesivo (más de 360 grados sería inválido)
                var boundsWidth = northeastLng - southwestLng;
                if (boundsWidth > 360m)
                {
                    throw new ArgumentException("Invalid bounds: longitude range cannot exceed 360 degrees");
                }
                
                // Validar rangos de coordenadas
                if (northeastLat < -90m || northeastLat > 90m ||
                    southwestLat < -90m || southwestLat > 90m ||
                    northeastLng < -180m || northeastLng > 180m ||
                    southwestLng < -180m || southwestLng > 180m)
                {
                    throw new ArgumentException("Invalid coordinate ranges. Latitude must be between -90 and 90, Longitude between -180 and 180");
                }
                
                // ✅ VALIDACIÓN ADICIONAL: Validar que los bounds sean razonables (no demasiado grandes)
                // ✅ CORREGIDO: Calcular lngDiff correctamente cuando hay valores negativos y positivos
                var latDiff = northeastLat - southwestLat;
                var lngDiff = northeastLng - southwestLng;
                // Si el resultado es negativo, significa que cruzamos el meridiano 180/-180
                if (lngDiff < 0)
                {
                    lngDiff = lngDiff + 360m;
                }
                var lngDiffAbs = Math.Abs(lngDiff);
                
                // ✅ AUMENTADO EL LÍMITE: 50 grados es demasiado estricto para zoom bajo (zoom 6 puede tener ~11 grados de lat y ~8 de lng)
                // Cambiar a 90 grados para permitir vistas continentales válidas
                if (latDiff > 90m || lngDiffAbs > 90m)
                {
                    throw new ArgumentException($"Bounds demasiado grandes: latDiff={latDiff}, lngDiff={lngDiffAbs}. El área visible no puede exceder 90 grados.");
                }
                
                // ✅ LOG PARA DEPURACIÓN: Ver qué bounds se reciben y cuántos servicios se filtran
                Console.WriteLine($"🔍 Bounds recibidos: NE({northeastLat}, {northeastLng}), SW({southwestLat}, {southwestLng}), Zoom={zoom}, Área={latDiff}x{lngDiffAbs} grados");

                // ✅ OPTIMIZACIÓN: Determinar límite según zoom (mejora performance con muchos datos)
                // Zoom alto = área pequeña = más servicios necesarios
                // Zoom bajo = área grande = menos servicios necesarios
                int maxResults = limit;
                bool isVeryLowZoom = false;
                if (zoom.HasValue)
                {
                    isVeryLowZoom = zoom.Value < 8; // Zoom muy bajo: continente/mundo
                    maxResults = zoom.Value switch
                    {
                        >= 18 => Math.Min(limit, 500),  // Zoom muy alto: barrio específico
                        >= 15 => Math.Min(limit, 200),   // Zoom alto: área pequeña
                        >= 12 => Math.Min(limit, 100),   // Zoom medio: ciudad
                        >= 10 => Math.Min(limit, 50),    // Zoom bajo: región
                        >= 8 => Math.Min(limit, 30),     // Zoom muy bajo: país
                        _ => Math.Min(limit, 50)         // Zoom extremadamente bajo: continente (aumentar límite)
                    };
                }
                
                // ✅ CORREGIDO: Calcular ancho de bounds para detectar áreas muy grandes
                // Si el ancho es > 180 grados, el área es tan grande que cubre casi todo el mundo
                // En ese caso, solo filtrar por latitud, no por longitud
                // ✅ CRÍTICO: Calcular correctamente cuando hay valores negativos y positivos
                // Si southwestLng es negativo y northeastLng es positivo, la diferencia es correcta
                // Ejemplo: 0.41 - (-7.82) = 8.23 grados (correcto)
                var boundsWidthLng = northeastLng - southwestLng;
                // ✅ Si el resultado es negativo, significa que cruzamos el meridiano 180/-180
                // En ese caso, calcular el ancho correctamente sumando 360
                if (boundsWidthLng < 0)
                {
                    boundsWidthLng = boundsWidthLng + 360m;
                }
                var isVeryLargeArea = boundsWidthLng > 180m;

                // ✅ OPTIMIZACIÓN CRÍTICA: Filtrar por bounds directamente en SQL usando CAST
                // Esto es 100-1000x más rápido que filtrar en memoria porque:
                // 1. Solo carga servicios necesarios desde BD (no todos)
                // 2. Usa índices de la base de datos
                // 3. Reduce memoria y transferencia de datos
                
                // ✅ Paso 1: Obtener IDs de servicios que cumplen los criterios usando SQL directo
                // Esto filtra por bounds directamente en SQL usando CAST
                // ✅ CORREGIDO: Usar parámetros SQL para evitar problemas de formato decimal y SQL injection
                // ✅ CORREGIDO: Manejar áreas muy grandes (zoom bajo) - solo filtrar por latitud si el ancho > 180 grados
                var sqlQuery = isVeryLargeArea
                    ? @"
                    SELECT ss.""Id""
                    FROM ""SearchServices"" ss
                    INNER JOIN ""ExpertProfiles"" ep ON ss.""ExpertProfileId"" = ep.""Id""
                    WHERE ss.""CategoryId"" = @categoryId
                      AND ss.""ServiceTypeId"" = @serviceTypeId
                      AND ss.""IsActive"" = true
                      AND ep.""IsOnVacation"" = false
                      -- 🛡️ T6 FIX: solo Approved+OnboardingCompleted O PendingVerification (alineado con filtro LINQ línea ~89)
                      AND ((ep.""StripeStatus"" = 2 AND ep.""OnboardingCompleted"" = true) OR ep.""StripeStatus"" = 6)
                      AND ep.""Latitude"" IS NOT NULL
                      AND ep.""Latitude"" != ''
                      AND ep.""Longitude"" IS NOT NULL
                      AND ep.""Longitude"" != ''
                      AND CAST(ep.""Latitude"" AS NUMERIC) >= @southwestLat
                      AND CAST(ep.""Latitude"" AS NUMERIC) <= @northeastLat
                    LIMIT @maxResults"
                    : @"
                    SELECT ss.""Id""
                    FROM ""SearchServices"" ss
                    INNER JOIN ""ExpertProfiles"" ep ON ss.""ExpertProfileId"" = ep.""Id""
                    WHERE ss.""CategoryId"" = @categoryId
                      AND ss.""ServiceTypeId"" = @serviceTypeId
                      AND ss.""IsActive"" = true
                      AND ep.""IsOnVacation"" = false
                      -- 🛡️ T6 FIX: solo Approved+OnboardingCompleted O PendingVerification (alineado con filtro LINQ línea ~89)
                      AND ((ep.""StripeStatus"" = 2 AND ep.""OnboardingCompleted"" = true) OR ep.""StripeStatus"" = 6)
                      AND ep.""Latitude"" IS NOT NULL
                      AND ep.""Latitude"" != ''
                      AND ep.""Longitude"" IS NOT NULL
                      AND ep.""Longitude"" != ''
                      AND CAST(ep.""Latitude"" AS NUMERIC) >= @southwestLat
                      AND CAST(ep.""Latitude"" AS NUMERIC) <= @northeastLat
                      AND (
                          -- Caso normal: bounds no cruza el meridiano
                          (CAST(ep.""Longitude"" AS NUMERIC) >= @southwestLng AND CAST(ep.""Longitude"" AS NUMERIC) <= @northeastLng)
                          OR
                          -- Caso especial: bounds cruza el meridiano 180/-180 (southwestLng > northeastLng)
                          (@southwestLng > @northeastLng AND (CAST(ep.""Longitude"" AS NUMERIC) >= @southwestLng OR CAST(ep.""Longitude"" AS NUMERIC) <= @northeastLng))
                      )
                    LIMIT @maxResults";

                // ✅ Usar parámetros SQL para evitar problemas de formato y SQL injection
                var serviceIds = new List<int>();
                using (var command = _context.Database.GetDbConnection().CreateCommand())
                {
                    command.CommandText = sqlQuery;
                    
                    // ✅ Agregar parámetros con tipos correctos
                    var categoryIdParam = command.CreateParameter();
                    categoryIdParam.ParameterName = "@categoryId";
                    categoryIdParam.Value = categoryId;
                    categoryIdParam.DbType = System.Data.DbType.Int32;
                    command.Parameters.Add(categoryIdParam);
                    
                    var serviceTypeIdParam = command.CreateParameter();
                    serviceTypeIdParam.ParameterName = "@serviceTypeId";
                    serviceTypeIdParam.Value = serviceTypeId;
                    serviceTypeIdParam.DbType = System.Data.DbType.Int32;
                    command.Parameters.Add(serviceTypeIdParam);
                    
                    var southwestLatParam = command.CreateParameter();
                    southwestLatParam.ParameterName = "@southwestLat";
                    southwestLatParam.Value = southwestLat;
                    southwestLatParam.DbType = System.Data.DbType.Decimal;
                    command.Parameters.Add(southwestLatParam);
                    
                    var northeastLatParam = command.CreateParameter();
                    northeastLatParam.ParameterName = "@northeastLat";
                    northeastLatParam.Value = northeastLat;
                    northeastLatParam.DbType = System.Data.DbType.Decimal;
                    command.Parameters.Add(northeastLatParam);
                    
                    var southwestLngParam = command.CreateParameter();
                    southwestLngParam.ParameterName = "@southwestLng";
                    southwestLngParam.Value = southwestLng;
                    southwestLngParam.DbType = System.Data.DbType.Decimal;
                    command.Parameters.Add(southwestLngParam);
                    
                    var northeastLngParam = command.CreateParameter();
                    northeastLngParam.ParameterName = "@northeastLng";
                    northeastLngParam.Value = northeastLng;
                    northeastLngParam.DbType = System.Data.DbType.Decimal;
                    command.Parameters.Add(northeastLngParam);
                    
                    var maxResultsParam = command.CreateParameter();
                    maxResultsParam.ParameterName = "@maxResults";
                    maxResultsParam.Value = maxResults;
                    maxResultsParam.DbType = System.Data.DbType.Int32;
                    command.Parameters.Add(maxResultsParam);
                    
                    // ❌ ELIMINADO: No expandir bounds - solo devolver servicios dentro del área visible exacta
                    // El usuario solo quiere ver los servicios que están realmente visibles en el mapa
                    
                    _context.Database.OpenConnection();
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            serviceIds.Add(reader.GetInt32(0));
                        }
                    }
                    _context.Database.CloseConnection();
                }

                // ✅ Paso 2: Cargar servicios completos con todas las relaciones usando los IDs filtrados
                // Esto es eficiente porque ya filtramos en SQL, solo cargamos lo necesario
                var services = serviceIds.Count > 0
                    ? await _context.SearchServices
                        .AsNoTracking()
                        .Where(ss => serviceIds.Contains(ss.Id))
                        .Include(ss => ss.Images)
                        .Include(ss => ss.ExpertProfile)
                            .ThenInclude(ep => ep.User)
                            .ThenInclude(u => u.ReviewsReceived)
                                .ThenInclude(r => r.Reviewer)
                        .Include(ss => ss.ExpertProfile)
                            .ThenInclude(ep => ep.User)
                            .ThenInclude(u => u.ReviewsReceived)
                                .ThenInclude(r => r.ImagesCollection)
                        .Include(ss => ss.ExpertProfile)
                            .ThenInclude(ep => ep.User)
                            .ThenInclude(u => u.ReviewsReceived)
                                .ThenInclude(r => r.SearchHire)
                        .Include(ss => ss.ExpertProfile)
                            .ThenInclude(ep => ep.User)
                            .ThenInclude(u => u.SearchHiresAsExpert)
                                .ThenInclude(sh => sh.Status)
                        .Include(ss => ss.Category)
                        .Include(ss => ss.ServiceType)
                            .ThenInclude(st => st.ServiceTypeCategory)
                        .Include(ss => ss.SelectedDeliverableTypes)
                            .ThenInclude(ssdt => ssdt.DeliverableType)
                        .ToListAsync(cancellationToken)
                    : new List<SearchService>(); // Si no hay IDs, devolver lista vacía

                // ✅ FILTRADO ADICIONAL EN MEMORIA: Asegurar que solo se devuelvan servicios dentro de los bounds exactos
                // Esto corrige cualquier problema con el filtrado SQL o con servicios que se cargaron incorrectamente
                var serviciosAntesFiltrado = services.Count;
                services = services.Where(ss =>
                {
                    if (!decimal.TryParse(ss.ExpertProfile.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLat) ||
                        !decimal.TryParse(ss.ExpertProfile.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLng))
                    {
                        return false; // Excluir servicios sin coordenadas válidas
                    }

                    // ✅ FILTRADO ESTRICTO: Solo servicios dentro de los bounds exactos
                    bool latInBounds = expertLat >= southwestLat && expertLat <= northeastLat;
                    
                    // Manejar el caso de longitudes que cruzan el meridiano 180/-180
                    bool lngInBounds;
                    if (southwestLng > northeastLng) // Bounds cruza el meridiano
                    {
                        lngInBounds = expertLng >= southwestLng || expertLng <= northeastLng;
                    }
                    else // Caso normal
                    {
                        lngInBounds = expertLng >= southwestLng && expertLng <= northeastLng;
                    }
                    
                    return latInBounds && lngInBounds;
                }).ToList();
                
                // ✅ LOG PARA DEBUGGING: Ver cuántos servicios se filtraron
                Console.WriteLine($"✅ FILTRADO POR BOUNDS - Servicios: {serviciosAntesFiltrado} antes, {services.Count} después. Bounds: NE({northeastLat}, {northeastLng}), SW({southwestLat}, {southwestLng})");

                // ✅ TotalCount DESPUÉS del filtrado en memoria (solo servicios dentro de bounds)
                // CRÍTICO: Este count debe reflejar SOLO los servicios que están realmente dentro de los bounds
                var totalCount = services.Count; // Ya está filtrado por bounds arriba
                
                // ✅ LOG PARA VERIFICAR
                Console.WriteLine($"✅ TotalCount después de filtrado: {totalCount} servicios dentro de bounds");

                // ✅ Ordenar por distancia al centro del bounds
                var centerLat = (northeastLat + southwestLat) / 2;
                var centerLng = (northeastLng + southwestLng) / 2;
                
                services = services.OrderBy(ss =>
                {
                    if (!decimal.TryParse(ss.ExpertProfile.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLat) ||
                        !decimal.TryParse(ss.ExpertProfile.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLng))
                    {
                        return decimal.MaxValue;
                    }
                    return CalculateDistance(centerLat, centerLng, expertLat, expertLng);
                }).ToList();

                // ✅ Aplicar límite después del filtrado y ordenamiento
                if (services.Count > maxResults)
                {
                    services = services.Take(maxResults).ToList();
                }

                // ✅ Cargar todas las disponibilidades activas de los expertos
                var expertProfileIds = services.Select(ss => ss.ExpertProfileId).Distinct().ToList();
                var availabilities = await _context.ExpertAvailabilities
                    .Where(ea => expertProfileIds.Contains(ea.ExpertId) && ea.IsActive && ea.EffectiveTo == null)
                    .OrderByDescending(ea => ea.EffectiveFrom)
                    .ToListAsync(cancellationToken);

                var availabilityByExpert = availabilities
                    .GroupBy(ea => ea.ExpertId)
                    .ToDictionary(g => g.Key, g => g.First());

                // ✅ Validar parámetros de paginación
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 100) pageSize = 50; // Máximo 100 por página
                
                // ✅ Total count ya calculado arriba después del filtrado estricto por bounds
                // totalCount ya está definido arriba después del filtrado en memoria (línea ~1000)
                
                // ✅ Mapear a SearchServiceDetailDto (información completa)
                var mappedServices = services.Select(ss => MapToDetailDto(ss, availabilityByExpert)).ToList();
                
                // ✅ Aplicar paginación
                var paginatedServices = mappedServices
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();
                
                return (paginatedServices, totalCount);
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
                // 🛡️ V4 FIX: Country gate (alineado con BecomeExpert P3 y UpdateExpertProfile E3).
                // Sin esto un perfil con Country=null o no-EEA puede crear servicio que nunca cobra.
                if (expertProfile != null && !Common.SupportedConnectCountries.IsSupported(expertProfile.Country))
                {
                    return (false, null, null);
                }
                if (expertProfile == null)
                {
                    return (false, null, null);
                }

                // 🛡️ T5 FIX: validar estado Stripe Connect ANTES de permitir crear servicio.
                // El filtro de búsqueda (línea ~89) ya excluye servicios cuyo experto no esté
                // operativo (Approved con OnboardingCompleted=true OR PendingVerification), pero
                // sin este gate al CREAR, un experto sin Stripe podía publicar un servicio que
                // luego NUNCA aparecería en búsquedas → mala UX. Peor aún: si el experto está
                // Deauthorized/Rejected/Restricted/Disabled, NUNCA recibirá transfers — cliente
                // podría llegar al checkout vía link directo y pagar; el cobro sí funciona pero
                // el transfer post-captura falla → dinero atascado en la plataforma.
                // Estados permitidos para CREAR servicio (alineado con el filtro de búsqueda):
                //   - Approved + OnboardingCompleted=true
                //   - PendingVerification (Stripe revisando documentación)
                var allowedForCreate =
                    (expertProfile.StripeStatus == StripeStatus.Approved && expertProfile.OnboardingCompleted) ||
                    expertProfile.StripeStatus == StripeStatus.PendingVerification;

                if (!allowedForCreate)
                {
                    // No usamos LogCritical (no es bug, es UX). Warning + return false claro.
                    // El controller convierte (false, null, null) en BadRequest con mensaje genérico,
                    // pero el log queda en BD para diagnóstico admin.
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

                // 🌍 Currency: normalizar el valor recibido. Si el experto tiene Stripe Account
                // activo, FORZAR la divisa al default del Stripe Account (verdad autoritativa e
                // inmutable). Esto cubre el caso de mudanza: experto se registró en US (Stripe
                // acct USD), cambia ExpertProfile.Country a ES → CreateService deriva EUR del país,
                // pero el Stripe acct sigue siendo USD. Sin esta corrección, el servicio se
                // crearía en EUR y cualquier transfer al experto fallaría con currency_mismatch
                // (RefundService.cs:1316-1342 → RequiresManualReview + dinero atascado).
                // 🛡️ Round 28 MUD-1: el Warning se convierte en CORRECCIÓN ACTIVA — preferimos
                // SIEMPRE la divisa del Stripe acct sobre la derivada del país del perfil.
                var resolvedCurrency = Common.SupportedCurrenciesList.Normalize(request.Currency) ?? "EUR";

                if (!string.IsNullOrEmpty(expertProfile.StripeAccountId))
                {
                    try
                    {
                        var stripeAccount = await new Stripe.AccountService().GetAsync(expertProfile.StripeAccountId);
                        var accountDefault = stripeAccount?.DefaultCurrency;
                        if (!string.IsNullOrEmpty(accountDefault))
                        {
                            var normalizedAccountDefault = accountDefault.Trim().ToUpperInvariant();
                            if (!string.Equals(normalizedAccountDefault, resolvedCurrency, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogWarning(
                                    "Currency mismatch on CreateSearchService — OVERRIDING: requested {RequestedCurrency} but Stripe Connect account {StripeAccountId} (expert {ExpertProfileId}) default_currency is {AccountDefaultCurrency}. Service will be created with {AccountDefaultCurrency} to avoid currency_mismatch in future transfers. This typically indicates a country relocation (e.g. expert moved from US→ES but Stripe account is still USD).",
                                    resolvedCurrency, expertProfile.StripeAccountId, expertProfile.Id, normalizedAccountDefault, normalizedAccountDefault);
                                resolvedCurrency = normalizedAccountDefault;
                            }
                        }
                    }
                    catch (Stripe.StripeException stripeEx) when (stripeEx.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Stripe acct ya no existe — el experto debería re-onboardar. Permitir creación con la divisa
                        // derivada del país; el guard de RefundService capturará el problema en el primer transfer.
                        _logger.LogWarning(
                            "Stripe Account {StripeAccountId} for expert {ExpertProfileId} not found (404). Allowing CreateSearchService with currency {RequestedCurrency} — expert should re-onboard.",
                            expertProfile.StripeAccountId, expertProfile.Id, resolvedCurrency);
                    }
                    catch (Exception stripeEx)
                    {
                        // No bloquear creación si Stripe no responde: la divisa se revalida en checkout.
                        _logger.LogWarning(stripeEx,
                            "Could not verify Stripe Connect default_currency for expert {ExpertProfileId} (account {StripeAccountId}). Allowing CreateSearchService with currency {RequestedCurrency}; will revalidate at checkout.",
                            expertProfile.Id, expertProfile.StripeAccountId, resolvedCurrency);
                    }
                }

                var searchService = new SearchService
                {
                    ExpertProfileId = request.ExpertProfileId,
                    CategoryId = request.CategoryId,
                    ServiceTypeId = request.ServiceTypeId,
                    Price = request.Price,
                    Currency = resolvedCurrency,
                    Conditions = request.Conditions,
                    DurationInHours = request.DurationInHours ?? 0,
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true
                };

                _context.SearchServices.Add(searchService);
                int serviceId = 0; // ✅ FIX: Guardar ID del servicio para usar en recovery
                try
                {
                    await _context.SaveChangesAsync();
                    serviceId = searchService.Id; // ✅ FIX: Guardar ID después de SaveChanges
                }
                catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                {
                    // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                    using var recoveryScope = _serviceScopeFactory.CreateScope();
                    var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var recoveryService = new SearchService
                    {
                        ExpertProfileId = request.ExpertProfileId,
                        CategoryId = request.CategoryId,
                        ServiceTypeId = request.ServiceTypeId,
                        Price = request.Price,
                        Currency = resolvedCurrency,
                        Conditions = request.Conditions,
                        DurationInHours = request.DurationInHours ?? 0,
                        CreatedAt = DateTime.UtcNow,
                        IsActive = true
                    };

                    recoveryContext.SearchServices.Add(recoveryService);
                    await recoveryContext.SaveChangesAsync();
                    serviceId = recoveryService.Id; // ✅ FIX: Guardar ID del servicio recuperado
                    searchService = recoveryService; // Actualizar referencia
                }
                catch (ObjectDisposedException disposedEx)
                {
                    // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                    using var recoveryScope = _serviceScopeFactory.CreateScope();
                    var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var recoveryService = new SearchService
                    {
                        ExpertProfileId = request.ExpertProfileId,
                        CategoryId = request.CategoryId,
                        ServiceTypeId = request.ServiceTypeId,
                        Price = request.Price,
                        Currency = resolvedCurrency,
                        Conditions = request.Conditions,
                        DurationInHours = request.DurationInHours ?? 0,
                        CreatedAt = DateTime.UtcNow,
                        IsActive = true
                    };

                    recoveryContext.SearchServices.Add(recoveryService);
                    await recoveryContext.SaveChangesAsync();
                    serviceId = recoveryService.Id; // ✅ FIX: Guardar ID del servicio recuperado
                    searchService = recoveryService; // Actualizar referencia
                }
                
                // ✅ VALIDACIÓN: Verificar que al menos un tipo de entregable esté seleccionado
                if (string.IsNullOrWhiteSpace(request.SelectedDeliverableTypes))
                {
                    return (false, null, null); // No se puede crear servicio sin tipos de entregables
                }

                int[] deliverableTypeIds;
                try
                {
                    deliverableTypeIds = System.Text.Json.JsonSerializer.Deserialize<int[]>(request.SelectedDeliverableTypes);
                    if (deliverableTypeIds == null || deliverableTypeIds.Length == 0)
                    {
                        return (false, null, null); // No se puede crear servicio sin tipos de entregables
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    return (false, null, null); // Formato inválido
                }

                // Procesar tipos de entregables seleccionados
                bool deliverableTypesRecoveryUsed = false;
                AppDbContext deliverableTypesContext = _context;
                
                if (!string.IsNullOrEmpty(request.SelectedDeliverableTypes))
                {
                    try
                    {
                        // deliverableTypeIds ya está declarado en el ámbito superior
                        deliverableTypeIds = System.Text.Json.JsonSerializer.Deserialize<int[]>(request.SelectedDeliverableTypes);
                        _logger.LogInformation("Creating deliverable types for service {ServiceId} with {Count} types: {Types}", serviceId, deliverableTypeIds?.Length ?? 0, string.Join(", ", deliverableTypeIds ?? Array.Empty<int>()));
                        
                        foreach (var deliverableTypeId in deliverableTypeIds)
                        {
                            var deliverableType = await deliverableTypesContext.DeliverableTypes.FindAsync(deliverableTypeId);
                            if (deliverableType != null)
                            {
                                var searchServiceDeliverableType = new SearchServiceDeliverableType
                                {
                                    SearchServiceId = serviceId, // ✅ FIX: Usar serviceId guardado en lugar de searchService.Id
                                    DeliverableTypeId = deliverableTypeId,
                                    IsSelected = true,
                                    CreatedAt = DateTime.UtcNow,
                                    UpdatedAt = DateTime.UtcNow
                                };
                                deliverableTypesContext.SearchServiceDeliverableTypes.Add(searchServiceDeliverableType);
                                _logger.LogInformation("Added deliverable type {DeliverableTypeId} ({Name}) to service {ServiceId}", deliverableTypeId, deliverableType.Name, serviceId);
                            }
                            else
                            {
                                _logger.LogWarning("Deliverable type {DeliverableTypeId} not found in database", deliverableTypeId);
                            }
                        }
                        
                        try
                        {
                            await deliverableTypesContext.SaveChangesAsync();
                            _logger.LogInformation("Successfully saved {Count} deliverable types for service {ServiceId}", deliverableTypeIds?.Length ?? 0, serviceId);
                        }
                        catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                        {
                            // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                            _logger.LogWarning("Connection disposed while saving deliverable types for service {ServiceId}, attempting recovery", serviceId);
                            using var recoveryScope = _serviceScopeFactory.CreateScope();
                            deliverableTypesContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                            deliverableTypesRecoveryUsed = true;
                            
                            // Re-crear los tipos de entregables en el nuevo contexto
                            var recoveryDeliverableTypeIds = System.Text.Json.JsonSerializer.Deserialize<int[]>(request.SelectedDeliverableTypes);
                            foreach (var deliverableTypeId in recoveryDeliverableTypeIds)
                            {
                                var deliverableType = await deliverableTypesContext.DeliverableTypes.FindAsync(deliverableTypeId);
                                if (deliverableType != null)
                                {
                                    var recoveryDeliverableType = new SearchServiceDeliverableType
                                    {
                                        SearchServiceId = serviceId, // ✅ FIX: Usar serviceId guardado
                                        DeliverableTypeId = deliverableTypeId,
                                        IsSelected = true,
                                        CreatedAt = DateTime.UtcNow,
                                        UpdatedAt = DateTime.UtcNow
                                    };
                                    deliverableTypesContext.SearchServiceDeliverableTypes.Add(recoveryDeliverableType);
                                }
                            }
                            await deliverableTypesContext.SaveChangesAsync();
                            _logger.LogInformation("Successfully recovered and saved {Count} deliverable types for service {ServiceId}", recoveryDeliverableTypeIds?.Length ?? 0, serviceId);
                        }
                        catch (ObjectDisposedException disposedEx)
                        {
                            // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                            _logger.LogWarning("Connection disposed while saving deliverable types for service {ServiceId}, attempting recovery", serviceId);
                            using var recoveryScope = _serviceScopeFactory.CreateScope();
                            deliverableTypesContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                            deliverableTypesRecoveryUsed = true;
                            
                            // Re-crear los tipos de entregables en el nuevo contexto
                            var recoveryDeliverableTypeIds2 = System.Text.Json.JsonSerializer.Deserialize<int[]>(request.SelectedDeliverableTypes);
                            foreach (var deliverableTypeId in recoveryDeliverableTypeIds2)
                            {
                                var deliverableType = await deliverableTypesContext.DeliverableTypes.FindAsync(deliverableTypeId);
                                if (deliverableType != null)
                                {
                                    var recoveryDeliverableType = new SearchServiceDeliverableType
                                    {
                                        SearchServiceId = serviceId, // ✅ FIX: Usar serviceId guardado
                                        DeliverableTypeId = deliverableTypeId,
                                        IsSelected = true,
                                        CreatedAt = DateTime.UtcNow,
                                        UpdatedAt = DateTime.UtcNow
                                    };
                                    deliverableTypesContext.SearchServiceDeliverableTypes.Add(recoveryDeliverableType);
                                }
                            }
                            await deliverableTypesContext.SaveChangesAsync();
                            _logger.LogInformation("Successfully recovered and saved {Count} deliverable types for service {ServiceId}", recoveryDeliverableTypeIds2?.Length ?? 0, serviceId);
                        }
                        
                        // ✅ FIX: Solo verificar si no se usó recovery (para evitar usar contexto disposed)
                        if (!deliverableTypesRecoveryUsed)
                        {
                            // Verificar que se guardaron correctamente
                            var savedDeliverableTypes = await deliverableTypesContext.SearchServiceDeliverableTypes
                                .Where(ssdt => ssdt.SearchServiceId == serviceId) // ✅ FIX: Usar serviceId guardado
                                .Include(ssdt => ssdt.DeliverableType)
                                .ToListAsync();
                            _logger.LogInformation("Verified {Count} deliverable types saved for service {ServiceId}", savedDeliverableTypes.Count, serviceId);
                        }
                    }
                    catch (Exception ex)
                    {
                        // ✅ FIX: Si falla el recovery o la verificación, continuar de todas formas
                        // El servicio principal ya está creado, los entregables son opcionales
                        _logger.LogError(ex, "CRITICAL: Failed to save deliverable types for service {ServiceId}, but service was created successfully. SelectedDeliverableTypes: {Types}", serviceId, request.SelectedDeliverableTypes);
                    }
                }
                else
                {
                }

                var imageUrls = new List<string>();
                if (request.Images != null && request.Images.Any())
                {
                    var bucketName = _configuration["GoogleCloud:BucketName"];
                    var uploadedImages = new List<(string ImageUrl, string ImageObjectName)>();
                    
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
                                Size = new Size(1600, 1600), // antes 200x200: causaba fotos pixeladas al mostrarlas a 360-720px
                                Mode = ResizeMode.Max
                            }));

                            using (var outputStream = new MemoryStream())
                            {
                                image.SaveAsJpeg(outputStream, new JpegEncoder { Quality = 88 }); // antes default (~75)
                                outputStream.Position = 0;
                                // ✅ FIX: Quitar PredefinedAcl cuando el bucket tiene uniform bucket-level access habilitado
                                // El acceso se controla mediante IAM policies del bucket, no ACLs por objeto
                                await _supabaseStorage.UploadAsync(
                                    _supabaseStorage.ImagesBucket,
                                    objectName,
                                    outputStream,
                                    "image/jpeg");
                            }
                        }

                        var imageUrl = _supabaseStorage.GetPublicUrl(_supabaseStorage.ImagesBucket, objectName);
                        uploadedImages.Add((imageUrl, objectName));
                        
                        var searchServiceImage = new SearchServiceImage
                        {
                            SearchServiceId = serviceId, // ✅ FIX: Usar serviceId guardado
                            ImageUrl = imageUrl,
                            ImageObjectName = objectName,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.SearchServiceImages.Add(searchServiceImage);
                        imageUrls.Add(ResolveServiceImageUrl(searchServiceImage));
                    }
                    
                    try
                    {
                        await _context.SaveChangesAsync();
                    }
                    catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                    {
                        // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                        try
                        {
                            using var recoveryScope = _serviceScopeFactory.CreateScope();
                            var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                            
                            // Re-crear las imágenes en el nuevo contexto (ya están subidas a GCS)
                            foreach (var (imageUrl, objectName) in uploadedImages)
                            {
                                var recoveryImage = new SearchServiceImage
                                {
                                    SearchServiceId = serviceId, // ✅ FIX: Usar serviceId guardado
                                    ImageUrl = imageUrl,
                                    ImageObjectName = objectName,
                                    CreatedAt = DateTime.UtcNow
                                };
                                recoveryContext.SearchServiceImages.Add(recoveryImage);
                            }
                            await recoveryContext.SaveChangesAsync();
                        }
                        catch (Exception recoveryEx)
                        {
                            // ✅ FIX: Si el recovery falla, loguear pero continuar
                            // Las imágenes ya están en GCS, solo falta registrarlas en BD
                            _logger.LogWarning(recoveryEx, "Failed to save images to database for service {ServiceId} after recovery, but images are uploaded to GCS", serviceId);
                        }
                    }
                    catch (ObjectDisposedException disposedEx)
                    {
                        // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                        try
                        {
                            using var recoveryScope = _serviceScopeFactory.CreateScope();
                            var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                            
                            // Re-crear las imágenes en el nuevo contexto (ya están subidas a GCS)
                            foreach (var (imageUrl, objectName) in uploadedImages)
                            {
                                var recoveryImage = new SearchServiceImage
                                {
                                    SearchServiceId = serviceId, // ✅ FIX: Usar serviceId guardado
                                    ImageUrl = imageUrl,
                                    ImageObjectName = objectName,
                                    CreatedAt = DateTime.UtcNow
                                };
                                recoveryContext.SearchServiceImages.Add(recoveryImage);
                            }
                            await recoveryContext.SaveChangesAsync();
                        }
                        catch (Exception recoveryEx)
                        {
                            // ✅ FIX: Si el recovery falla, loguear pero continuar
                            // Las imágenes ya están en GCS, solo falta registrarlas en BD
                            _logger.LogWarning(recoveryEx, "Failed to save images to database for service {ServiceId} after recovery, but images are uploaded to GCS", serviceId);
                        }
                    }
                }

                return (true, searchService, imageUrls);
            }
            catch (Exception ex)
            {
                // ✅ FIX: Si el servicio principal se creó pero falló algo después, verificar si existe
                // Si existe, retornar éxito parcial (el servicio está creado aunque falten entregables/imágenes)
                try
                {
                    using var checkScope = _serviceScopeFactory.CreateScope();
                    var checkContext = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var existingService = await checkContext.SearchServices
                        .FirstOrDefaultAsync(ss => ss.ExpertProfileId == request.ExpertProfileId 
                            && ss.CategoryId == request.CategoryId 
                            && ss.ServiceTypeId == request.ServiceTypeId
                            && ss.Price == request.Price
                            && ss.IsActive == true
                            && ss.CreatedAt >= DateTime.UtcNow.AddMinutes(-5)); // Servicio creado en los últimos 5 minutos
                    
                    if (existingService != null)
                    {
                        // ✅ El servicio se creó exitosamente, retornar éxito aunque haya fallado algo después
                        _logger.LogWarning(ex, "Service {ServiceId} was created successfully but encountered errors during deliverable types/images save. Service exists and is active.", existingService.Id);
                        return (true, existingService, new List<string>()); // Retornar éxito con lista vacía de imágenes
                    }
                }
                catch
                {
                    // Si no se puede verificar, lanzar el error original
                }
                
                // Si el servicio no existe, lanzar el error
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
                // 🛡️ Round 28: emitir Currency real del servicio (no asumir EUR en cards/panel).
                Currency = string.IsNullOrWhiteSpace(ss.Currency) ? "EUR" : ss.Currency.Trim().ToUpperInvariant(),
                Conditions = ss.Conditions,
                DurationInHours = ss.DurationInHours ?? 0,
                CreatedAt = ss.CreatedAt,
                IsActive = ss.IsActive,
                ImageUrls = ss.Images?
                    .Select(img => ResolveServiceImageUrl(img))
                    .Where(url => !string.IsNullOrEmpty(url))
                    .ToList() ?? new List<string>(),
                Images = ss.Images?
                    .Select(img => new SearchServiceImageDto
                    {
                        Id = img.Id,
                        Url = ResolveServiceImageUrl(img)
                    })
                    .Where(img => !string.IsNullOrEmpty(img.Url))
                    .ToList() ?? new List<SearchServiceImageDto>(),
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
                    WorkRadiusKm = ss.ExpertProfile.WorkRadiusKm,
                    StripeStatus = ss.ExpertProfile.StripeStatus, // ✅ CORRECCIÓN: Mapear StripeStatus
                    StripeStatusDetails = ss.ExpertProfile.StripeStatusDetails, // ✅ CORRECCIÓN: Mapear StripeStatusDetails
                    OnboardingCompleted = ss.ExpertProfile.OnboardingCompleted, // ✅ CORRECCIÓN: Mapear OnboardingCompleted
                    IsOnVacation = ss.ExpertProfile.IsOnVacation,
                    CurrentAvailability = availabilityDto, // ✅ NUEVO: Incluir horarios de disponibilidad
                    // ✅ FUTURE REQUIREMENTS
                    StripeFutureRequirements = ss.ExpertProfile.StripeFutureRequirements,
                    StripeFutureDueAt = ss.ExpertProfile.StripeFutureDueAt,
                    // ✅ INTERNACIONALIZACIÓN: Timezone, país y ciudad del experto
                    Timezone = ss.ExpertProfile.Timezone,
                    Country = ss.ExpertProfile.Country,
                    City = ss.ExpertProfile.City
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
                Currency = baseDto.Currency, // 🛡️ Round 28: copiar Currency al DTO de detalle
                Conditions = baseDto.Conditions,
                DurationInHours = baseDto.DurationInHours,
                CreatedAt = baseDto.CreatedAt,
                IsActive = baseDto.IsActive, // ✅ CORRECCIÓN: Incluir IsActive
                ImageUrls = baseDto.ImageUrls,
                Expert = baseDto.Expert,
                SelectedDeliverableTypes = baseDto.SelectedDeliverableTypes,
                CategoryName = ss.Category?.Name ?? "Unknown Category",
                CompletedSearches = ss.ExpertProfile?.User?.SearchHiresAsExpert?.Count(sh => sh.Status != null && sh.Status.StatusValue == "completed") ?? 0,
                AverageRating = ss.ExpertProfile?.User?.ReviewsReceived != null && ss.ExpertProfile.User.ReviewsReceived.Any()
                    ? ss.ExpertProfile.User.ReviewsReceived.Average(r => r.Score)
                    : 0,
                TotalReviews = ss.ExpertProfile?.User?.ReviewsReceived?.Count ?? 0 // ✅ NUEVO: Total de reseñas
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
                // 🛡️ Round 28: emitir Currency real del servicio.
                Currency = string.IsNullOrWhiteSpace(ss.Currency) ? "EUR" : ss.Currency.Trim().ToUpperInvariant(),
                Conditions = ss.Conditions,
                DurationInHours = ss.DurationInHours ?? 0,
                CreatedAt = ss.CreatedAt,
                IsActive = ss.IsActive,
                ImageUrls = ss.Images?
                    .Select(img => ResolveServiceImageUrl(img))
                    .Where(url => !string.IsNullOrEmpty(url))
                    .ToList() ?? new List<string>(),
                Images = ss.Images?
                    .Select(img => new SearchServiceImageDto
                    {
                        Id = img.Id,
                        Url = ResolveServiceImageUrl(img)
                    })
                    .Where(img => !string.IsNullOrEmpty(img.Url))
                    .ToList() ?? new List<SearchServiceImageDto>(),
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
                    WorkRadiusKm = ss.ExpertProfile.WorkRadiusKm,
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
            int expertProfileIdForRecovery = 0; // Para usar en recovery si es necesario
            try
            {
                // Verificar que el servicio existe y pertenece al usuario
                var existingService = await _context.SearchServices
                    .Include(ss => ss.ExpertProfile)
                    .FirstOrDefaultAsync(ss => ss.Id == request.ServiceId && ss.ExpertProfile.UserId == userId);
                
                // Guardar ExpertProfileId para usar en recovery si es necesario
                if (existingService != null)
                {
                    expertProfileIdForRecovery = existingService.ExpertProfileId ?? 0;
                }

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

                // 🛡️ Round 28 MUD-AH (P0): re-validar Currency contra Stripe acct.DefaultCurrency.
                // R27-T27-1-4 fija preservar la Currency del row existente, PERO si el row legacy
                // tenía la moneda incorrecta (típicamente porque se creó antes de MUD-1, o porque
                // el experto se mudó), ESTA es la oportunidad de auto-rebalancear. Sin esto, cada
                // edit perpetuaba el currency_mismatch y bloqueaba transfers indefinidamente.
                var currencyToUse = existingService.Currency ?? "EUR";
                if (!string.IsNullOrEmpty(existingService.ExpertProfile?.StripeAccountId))
                {
                    try
                    {
                        var acctSvc = new Stripe.AccountService();
                        var acct = await acctSvc.GetAsync(existingService.ExpertProfile.StripeAccountId);
                        var acctDefault = (acct?.DefaultCurrency ?? "").Trim().ToUpperInvariant();
                        if (!string.IsNullOrEmpty(acctDefault) && acctDefault != currencyToUse.ToUpperInvariant())
                        {
                            _logger.LogWarning(
                                "MUD-AH: SearchService {ServiceId} Currency rebased on edit. BD='{Before}' Stripe acct DefaultCurrency='{After}'. Override evita currency_mismatch en próximo hire.",
                                existingService.Id, currencyToUse, acctDefault);
                            currencyToUse = acctDefault;
                        }
                    }
                    catch (Stripe.StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Acct 404 → mantener Currency original (no podemos re-validar).
                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogWarning(ex, "MUD-AH: Stripe acct read failed during UpdateSearchService currency rebase");
                    }
                }

                // Paso 2: Crear el nuevo servicio con los datos actualizados
                // 🛡️ Round 27 — R27-T27-1-4 FIX: copiar Currency del servicio original (ahora con
                // posible rebase MUD-AH si divergía de Stripe acct).
                var newSearchService = new SearchService
                {
                    ExpertProfileId = existingService.ExpertProfileId, // Mantener el mismo ExpertProfile
                    CategoryId = request.CategoryId,
                    ServiceTypeId = request.ServiceTypeId,
                    Price = request.Price,
                    Currency = currencyToUse, // ← R27-T27-1-4 + MUD-AH
                    Conditions = request.Conditions,
                    DurationInHours = request.DurationInHours ?? 0,
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true
                };

                _context.SearchServices.Add(newSearchService);
                int newServiceId = 0; // ✅ FIX: Guardar ID del nuevo servicio para usar en recovery
                try
                {
                    await _context.SaveChangesAsync();
                    newServiceId = newSearchService.Id; // ✅ FIX: Guardar ID después de SaveChanges
                }
                catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                {
                    // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                    using var recoveryScope = _serviceScopeFactory.CreateScope();
                    var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    
                    // Inactivar servicio existente en nuevo contexto
                    var recoveryExistingService = await recoveryContext.SearchServices
                        .FirstOrDefaultAsync(ss => ss.Id == request.ServiceId);
                    if (recoveryExistingService != null)
                    {
                        recoveryExistingService.IsActive = false;
                    }
                    
                    // Crear nuevo servicio en nuevo contexto
                    // 🛡️ Round 27 — R27-T27-1-4 FIX: copiar Currency (rama de recovery DbUpdateException).
                    var recoveryService = new SearchService
                    {
                        ExpertProfileId = existingService.ExpertProfileId,
                        CategoryId = request.CategoryId,
                        ServiceTypeId = request.ServiceTypeId,
                        Price = request.Price,
                        Currency = currencyToUse, // ← R27-T27-1-4 + MUD-AH (rebase si Stripe acct divergía)
                        Conditions = request.Conditions,
                        DurationInHours = request.DurationInHours ?? 0,
                        CreatedAt = DateTime.UtcNow,
                        IsActive = true
                    };

                    recoveryContext.SearchServices.Add(recoveryService);
                    await recoveryContext.SaveChangesAsync();
                    newServiceId = recoveryService.Id; // ✅ FIX: Guardar ID del servicio recuperado
                    newSearchService = recoveryService; // Actualizar referencia
                }
                catch (ObjectDisposedException disposedEx)
                {
                    // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                    using var recoveryScope = _serviceScopeFactory.CreateScope();
                    var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();

                    // Inactivar servicio existente en nuevo contexto
                    var recoveryExistingService = await recoveryContext.SearchServices
                        .FirstOrDefaultAsync(ss => ss.Id == request.ServiceId);
                    if (recoveryExistingService != null)
                    {
                        recoveryExistingService.IsActive = false;
                    }

                    // Crear nuevo servicio en nuevo contexto
                    // 🛡️ Round 27 — R27-T27-1-4 FIX: copiar Currency (rama de recovery ObjectDisposed).
                    var recoveryService = new SearchService
                    {
                        ExpertProfileId = existingService.ExpertProfileId,
                        CategoryId = request.CategoryId,
                        ServiceTypeId = request.ServiceTypeId,
                        Price = request.Price,
                        Currency = currencyToUse, // ← R27-T27-1-4 + MUD-AH (rebase si Stripe acct divergía)
                        Conditions = request.Conditions,
                        DurationInHours = request.DurationInHours ?? 0,
                        CreatedAt = DateTime.UtcNow,
                        IsActive = true
                    };

                    recoveryContext.SearchServices.Add(recoveryService);
                    await recoveryContext.SaveChangesAsync();
                    newServiceId = recoveryService.Id; // ✅ FIX: Guardar ID del servicio recuperado
                    newSearchService = recoveryService; // Actualizar referencia
                }
                // ✅ VALIDACIÓN: Verificar que al menos un tipo de entregable esté seleccionado
                if (string.IsNullOrWhiteSpace(request.SelectedDeliverableTypes))
                {
                    return (false, null, null); // No se puede actualizar servicio sin tipos de entregables
                }

                int[] deliverableTypeIds;
                try
                {
                    deliverableTypeIds = System.Text.Json.JsonSerializer.Deserialize<int[]>(request.SelectedDeliverableTypes);
                    if (deliverableTypeIds == null || deliverableTypeIds.Length == 0)
                    {
                        return (false, null, null); // No se puede actualizar servicio sin tipos de entregables
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    return (false, null, null); // Formato inválido
                }

                // Paso 3: Procesar tipos de entregables seleccionados
                bool deliverableTypesRecoveryUsed = false;
                AppDbContext deliverableTypesContext = _context;
                
                if (!string.IsNullOrEmpty(request.SelectedDeliverableTypes))
                {
                    try
                    {
                        // deliverableTypeIds ya está declarado en el ámbito superior
                        deliverableTypeIds = System.Text.Json.JsonSerializer.Deserialize<int[]>(request.SelectedDeliverableTypes);
                        _logger.LogInformation("Creating deliverable types for updated service {ServiceId} with {Count} types: {Types}", newServiceId, deliverableTypeIds?.Length ?? 0, string.Join(", ", deliverableTypeIds ?? Array.Empty<int>()));
                        
                        foreach (var deliverableTypeId in deliverableTypeIds)
                        {
                            var deliverableType = await deliverableTypesContext.DeliverableTypes.FindAsync(deliverableTypeId);
                            if (deliverableType != null)
                            {
                                var searchServiceDeliverableType = new SearchServiceDeliverableType
                                {
                                    SearchServiceId = newServiceId, // ✅ FIX: Usar newServiceId guardado en lugar de newSearchService.Id
                                    DeliverableTypeId = deliverableTypeId,
                                    IsSelected = true,
                                    CreatedAt = DateTime.UtcNow,
                                    UpdatedAt = DateTime.UtcNow
                                };
                                deliverableTypesContext.SearchServiceDeliverableTypes.Add(searchServiceDeliverableType);
                                _logger.LogInformation("Added deliverable type {DeliverableTypeId} ({Name}) to updated service {ServiceId}", deliverableTypeId, deliverableType.Name, newServiceId);
                            }
                            else
                            {
                                _logger.LogWarning("Deliverable type {DeliverableTypeId} not found in database", deliverableTypeId);
                            }
                        }
                        
                        try
                        {
                            await deliverableTypesContext.SaveChangesAsync();
                            _logger.LogInformation("Successfully saved {Count} deliverable types for updated service {ServiceId}", deliverableTypeIds?.Length ?? 0, newServiceId);
                        }
                        catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                        {
                            // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                            _logger.LogWarning("Connection disposed while saving deliverable types for updated service {ServiceId}, attempting recovery", newServiceId);
                            using var recoveryScope = _serviceScopeFactory.CreateScope();
                            deliverableTypesContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                            deliverableTypesRecoveryUsed = true;
                            
                            // Re-crear los tipos de entregables en el nuevo contexto
                            var recoveryDeliverableTypeIds = System.Text.Json.JsonSerializer.Deserialize<int[]>(request.SelectedDeliverableTypes);
                            foreach (var deliverableTypeId in recoveryDeliverableTypeIds)
                            {
                                var deliverableType = await deliverableTypesContext.DeliverableTypes.FindAsync(deliverableTypeId);
                                if (deliverableType != null)
                                {
                                    var recoveryDeliverableType = new SearchServiceDeliverableType
                                    {
                                        SearchServiceId = newServiceId, // ✅ FIX: Usar newServiceId guardado
                                        DeliverableTypeId = deliverableTypeId,
                                        IsSelected = true,
                                        CreatedAt = DateTime.UtcNow,
                                        UpdatedAt = DateTime.UtcNow
                                    };
                                    deliverableTypesContext.SearchServiceDeliverableTypes.Add(recoveryDeliverableType);
                                }
                            }
                            await deliverableTypesContext.SaveChangesAsync();
                            _logger.LogInformation("Successfully recovered and saved {Count} deliverable types for updated service {ServiceId}", recoveryDeliverableTypeIds?.Length ?? 0, newServiceId);
                        }
                        catch (ObjectDisposedException disposedEx)
                        {
                            // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                            _logger.LogWarning("Connection disposed while saving deliverable types for updated service {ServiceId}, attempting recovery", newServiceId);
                            using var recoveryScope = _serviceScopeFactory.CreateScope();
                            deliverableTypesContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                            deliverableTypesRecoveryUsed = true;
                            
                            // Re-crear los tipos de entregables en el nuevo contexto
                            var recoveryDeliverableTypeIds2 = System.Text.Json.JsonSerializer.Deserialize<int[]>(request.SelectedDeliverableTypes);
                            foreach (var deliverableTypeId in recoveryDeliverableTypeIds2)
                            {
                                var deliverableType = await deliverableTypesContext.DeliverableTypes.FindAsync(deliverableTypeId);
                                if (deliverableType != null)
                                {
                                    var recoveryDeliverableType = new SearchServiceDeliverableType
                                    {
                                        SearchServiceId = newServiceId, // ✅ FIX: Usar newServiceId guardado
                                        DeliverableTypeId = deliverableTypeId,
                                        IsSelected = true,
                                        CreatedAt = DateTime.UtcNow,
                                        UpdatedAt = DateTime.UtcNow
                                    };
                                    deliverableTypesContext.SearchServiceDeliverableTypes.Add(recoveryDeliverableType);
                                }
                            }
                            await deliverableTypesContext.SaveChangesAsync();
                            _logger.LogInformation("Successfully recovered and saved {Count} deliverable types for updated service {ServiceId}", recoveryDeliverableTypeIds2?.Length ?? 0, newServiceId);
                        }
                        
                        // ✅ FIX: Solo verificar si no se usó recovery (para evitar usar contexto disposed)
                        if (!deliverableTypesRecoveryUsed)
                        {
                            // Verificar que se guardaron correctamente
                            var savedDeliverableTypes = await deliverableTypesContext.SearchServiceDeliverableTypes
                                .Where(ssdt => ssdt.SearchServiceId == newServiceId) // ✅ FIX: Usar newServiceId guardado
                                .Include(ssdt => ssdt.DeliverableType)
                                .ToListAsync();
                            _logger.LogInformation("Verified {Count} deliverable types saved for updated service {ServiceId}", savedDeliverableTypes.Count, newServiceId);
                        }
                    }
                    catch (Exception ex)
                    {
                        // ✅ FIX: Si falla el recovery o la verificación, continuar de todas formas
                        // El servicio principal ya está creado, los entregables son opcionales
                        _logger.LogError(ex, "CRITICAL: Failed to save deliverable types for updated service {ServiceId}, but service was updated successfully. SelectedDeliverableTypes: {Types}", newServiceId, request.SelectedDeliverableTypes);
                    }
                }
                else
                {
                }

                // Paso 4: Procesar las imágenes
                var imageUrls = new List<string>();
                
                // ✅ NUEVO: Cargar imágenes existentes del servicio anterior
                var existingImages = await _context.SearchServiceImages
                    .Where(img => img.SearchServiceId == existingService.Id)
                    .ToListAsync();
                
                // ✅ NUEVO: Procesar imágenes a eliminar explícitamente
                List<int> imagesToDeleteIds = new List<int>();
                if (!string.IsNullOrWhiteSpace(request.ImagesToDelete))
                {
                    try
                    {
                        imagesToDeleteIds = System.Text.Json.JsonSerializer.Deserialize<List<int>>(request.ImagesToDelete) ?? new List<int>();
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        _logger.LogWarning("Invalid format for ImagesToDelete in UpdateSearchService request. ServiceId: {ServiceId}", request.ServiceId);
                    }
                }
                
                // ✅ NUEVO: Conservar imágenes existentes que NO se eliminan explícitamente
                var imagesToKeep = existingImages
                    .Where(img => !imagesToDeleteIds.Contains(img.Id))
                    .ToList();
                
                // ✅ NUEVO: Copiar imágenes conservadas al nuevo servicio
                foreach (var existingImage in imagesToKeep)
                {
                    var copiedImage = new SearchServiceImage
                    {
                        SearchServiceId = newServiceId,
                        ImageUrl = existingImage.ImageUrl,
                        ImageObjectName = existingImage.ImageObjectName,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.SearchServiceImages.Add(copiedImage);
                    imageUrls.Add(ResolveServiceImageUrl(copiedImage));
                }
                
                // ✅ NUEVO: Agregar nuevas imágenes si se proporcionaron
                var uploadedImages = new List<(string ImageUrl, string ImageObjectName)>();
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
                                Size = new Size(1600, 1600), // antes 200x200: causaba fotos pixeladas al mostrarlas a 360-720px
                                Mode = ResizeMode.Max
                            }));

                            using (var outputStream = new MemoryStream())
                            {
                                image.SaveAsJpeg(outputStream, new JpegEncoder { Quality = 88 }); // antes default (~75)
                                outputStream.Position = 0;
                                // ✅ FIX: Quitar PredefinedAcl cuando el bucket tiene uniform bucket-level access habilitado
                                // El acceso se controla mediante IAM policies del bucket, no ACLs por objeto
                                await _supabaseStorage.UploadAsync(
                                    _supabaseStorage.ImagesBucket,
                                    objectName,
                                    outputStream,
                                    "image/jpeg");
                            }
                        }

                        var imageUrl = _supabaseStorage.GetPublicUrl(_supabaseStorage.ImagesBucket, objectName);
                        uploadedImages.Add((imageUrl, objectName));
                        
                        var searchServiceImage = new SearchServiceImage
                        {
                            SearchServiceId = newServiceId, // ✅ FIX: Usar newServiceId guardado
                            ImageUrl = imageUrl,
                            ImageObjectName = objectName,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.SearchServiceImages.Add(searchServiceImage);
                        imageUrls.Add(ResolveServiceImageUrl(searchServiceImage));
                    }
                }
                
                // ✅ NUEVO: Guardar todas las imágenes (conservadas + nuevas) en una sola transacción
                if (imagesToKeep.Any() || uploadedImages.Any())
                {
                    try
                    {
                        await _context.SaveChangesAsync();
                    }
                    catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                    {
                        // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                        try
                        {
                            using var recoveryScope = _serviceScopeFactory.CreateScope();
                            var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                            
                            // Re-crear las imágenes conservadas en el nuevo contexto
                            foreach (var existingImage in imagesToKeep)
                            {
                                var recoveryImage = new SearchServiceImage
                                {
                                    SearchServiceId = newServiceId,
                                    ImageUrl = existingImage.ImageUrl,
                                    ImageObjectName = existingImage.ImageObjectName,
                                    CreatedAt = DateTime.UtcNow
                                };
                                recoveryContext.SearchServiceImages.Add(recoveryImage);
                            }
                            
                            // Re-crear las nuevas imágenes en el nuevo contexto (ya están subidas a GCS)
                            foreach (var (imageUrl, objectName) in uploadedImages)
                            {
                                var recoveryImage = new SearchServiceImage
                                {
                                    SearchServiceId = newServiceId,
                                    ImageUrl = imageUrl,
                                    ImageObjectName = objectName,
                                    CreatedAt = DateTime.UtcNow
                                };
                                recoveryContext.SearchServiceImages.Add(recoveryImage);
                            }
                            
                            await recoveryContext.SaveChangesAsync();
                        }
                        catch (Exception recoveryEx)
                        {
                            // ✅ FIX: Si el recovery falla, loguear pero continuar
                            _logger.LogWarning(recoveryEx, "Failed to save images to database for updated service {ServiceId} after recovery", newServiceId);
                        }
                    }
                    catch (ObjectDisposedException disposedEx)
                    {
                        // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar con nuevo contexto
                        try
                        {
                            using var recoveryScope = _serviceScopeFactory.CreateScope();
                            var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
                            
                            // Re-crear las imágenes conservadas en el nuevo contexto
                            foreach (var existingImage in imagesToKeep)
                            {
                                var recoveryImage = new SearchServiceImage
                                {
                                    SearchServiceId = newServiceId,
                                    ImageUrl = existingImage.ImageUrl,
                                    ImageObjectName = existingImage.ImageObjectName,
                                    CreatedAt = DateTime.UtcNow
                                };
                                recoveryContext.SearchServiceImages.Add(recoveryImage);
                            }
                            
                            // Re-crear las nuevas imágenes en el nuevo contexto (ya están subidas a GCS)
                            foreach (var (imageUrl, objectName) in uploadedImages)
                            {
                                var recoveryImage = new SearchServiceImage
                                {
                                    SearchServiceId = newServiceId,
                                    ImageUrl = imageUrl,
                                    ImageObjectName = objectName,
                                    CreatedAt = DateTime.UtcNow
                                };
                                recoveryContext.SearchServiceImages.Add(recoveryImage);
                            }
                            
                            await recoveryContext.SaveChangesAsync();
                        }
                        catch (Exception recoveryEx)
                        {
                            // ✅ FIX: Si el recovery falla, loguear pero continuar
                            _logger.LogWarning(recoveryEx, "Failed to save images to database for updated service {ServiceId} after recovery", newServiceId);
                        }
                    }
                }

                return (true, newSearchService, imageUrls);
            }
            catch (Exception ex)
            {
                // ✅ FIX: Si el servicio principal se actualizó pero falló algo después, verificar si existe
                // Si existe, retornar éxito parcial (el servicio está actualizado aunque falten entregables/imágenes)
                try
                {
                    using var checkScope = _serviceScopeFactory.CreateScope();
                    var checkContext = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    // ✅ FIX: Usar expertProfileIdForRecovery que guardamos al inicio
                    var updatedService = await checkContext.SearchServices
                        .FirstOrDefaultAsync(ss => ss.ExpertProfileId == expertProfileIdForRecovery 
                            && ss.CategoryId == request.CategoryId 
                            && ss.ServiceTypeId == request.ServiceTypeId
                            && ss.Price == request.Price
                            && ss.IsActive == true
                            && ss.CreatedAt >= DateTime.UtcNow.AddMinutes(-5)); // Servicio actualizado en los últimos 5 minutos
                    
                    if (updatedService != null)
                    {
                        // ✅ El servicio se actualizó exitosamente, retornar éxito aunque haya fallado algo después
                        _logger.LogWarning(ex, "Service {ServiceId} was updated successfully but encountered errors during deliverable types/images save. Service exists and is active.", updatedService.Id);
                        return (true, updatedService, new List<string>()); // Retornar éxito con lista vacía de imágenes
                    }
                }
                catch
                {
                    // Si no se puede verificar, lanzar el error original
                }
                
                // Si el servicio no existe, lanzar el error
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

                // 🛡️ Round 13 — N7 FIX: bloquear el delete si hay hires activos asociados.
                // Antes: el delete pasaba siempre y dejaba hires vivos sobre un servicio "fantasma"
                // (IsActive=false) que el cliente seguía viendo en su panel sin entender qué pasó.
                // El modelo ya tenía la intención (FK SearchHire→SearchService con DeleteBehavior.Restrict),
                // pero el soft delete eludía esa protección.
                //
                // Política: NO permitir desactivar el servicio si tiene hires en estados no-finalizadores
                // (pending, awaiting_client_decision, disputed). El experto debe esperar a que se cierren
                // (o cancelarlas explícitamente) antes de retirar el servicio del catálogo.
                var openHiresCount = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Where(sh => sh.SearchServiceId == serviceId && sh.Status != null && !sh.Status.IsFinalizationStatus)
                    .CountAsync();

                if (openHiresCount > 0)
                {
                    // No marcamos inactivo. El controller debe interpretar `false` como "no se pudo borrar"
                    // y mostrar un mensaje al usuario.
                    return false;
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

            // ✅ Si la URL es externa (no de Google Cloud Storage), devolverla directamente
            if (!string.IsNullOrWhiteSpace(image.ImageUrl))
            {
                var bucketName = _configuration["GoogleCloud:BucketName"];
                var isExternalUrl = string.IsNullOrWhiteSpace(bucketName) || 
                                   !image.ImageUrl.Contains($"storage.googleapis.com/{bucketName}", StringComparison.OrdinalIgnoreCase);
                
                if (isExternalUrl)
                {
                    // URL externa (Unsplash, Pexels, etc.) - devolver directamente sin signed URL
                    return image.ImageUrl;
                }
            }

            // ✅ Si es URL de Google Cloud Storage o hay ImageObjectName, generar signed URL
            var fallback = string.IsNullOrWhiteSpace(image.ImageUrl) ? string.Empty : image.ImageUrl;
            return _signedUrlService.GetSignedUrl(image.ImageObjectName ?? string.Empty) ?? fallback;
        }

        private string ResolveReviewImageUrl(ReviewImage? image)
        {
            if (image == null)
            {
                return string.Empty;
            }

            // ✅ PRIORIDAD 1: Si la URL es de Unsplash u otro servicio externo, usar directamente (IGNORAR ImageObjectName)
            if (!string.IsNullOrWhiteSpace(image.ImageUrl))
            {
                var imageUrlLower = image.ImageUrl.ToLowerInvariant();
                if (imageUrlLower.Contains("unsplash.com") || 
                    imageUrlLower.Contains("pexels.com") || 
                    imageUrlLower.Contains("picsum.photos") ||
                    imageUrlLower.Contains("http://") ||
                    imageUrlLower.Contains("https://"))
                {
                    // Si es URL externa, NO usar ImageObjectName para generar signed URL
                    return image.ImageUrl;
                }
            }

            // ✅ PRIORIDAD 2: Si no es URL externa y hay ImageObjectName, intentar generar URL firmada del bucket
            if (!string.IsNullOrWhiteSpace(image.ImageObjectName))
            {
                var signedUrl = _signedUrlService.GetSignedUrl(image.ImageObjectName);
                if (!string.IsNullOrWhiteSpace(signedUrl))
                {
                    return signedUrl;
                }
            }

            // ✅ PRIORIDAD 3: Fallback a ImageUrl si existe
            return string.IsNullOrWhiteSpace(image.ImageUrl) ? string.Empty : image.ImageUrl;
        }

        /// <summary>
        /// Diccionario de coordenadas de capitales por código de país (ISO 3166-1 alpha-2)
        /// </summary>
        private static readonly Dictionary<string, (decimal Latitude, decimal Longitude)> CapitalCoordinates = new()
        {
            { "ES", (40.4168m, -3.7038m) },      // Madrid, España
            { "MX", (19.4326m, -99.1332m) },     // Ciudad de México, México
            { "US", (38.9072m, -77.0369m) },     // Washington D.C., Estados Unidos
            { "AR", (-34.6037m, -58.3816m) },    // Buenos Aires, Argentina
            { "CO", (4.7110m, -74.0721m) },     // Bogotá, Colombia
            { "CL", (-33.4489m, -70.6693m) },    // Santiago, Chile
            { "PE", (-12.0464m, -77.0428m) },    // Lima, Perú
            { "VE", (10.4806m, -66.9036m) },     // Caracas, Venezuela
            { "EC", (-0.1807m, -78.4678m) },     // Quito, Ecuador
            { "BO", (-16.2902m, -63.5887m) },    // La Paz, Bolivia
            { "PY", (-25.2637m, -57.5759m) },    // Asunción, Paraguay
            { "UY", (-34.9011m, -56.1645m) },    // Montevideo, Uruguay
            { "BR", (-15.7942m, -47.8822m) },    // Brasilia, Brasil
            { "PT", (38.7223m, -9.1393m) },      // Lisboa, Portugal
            { "FR", (48.8566m, 2.3522m) },      // París, Francia
            { "IT", (41.9028m, 12.4964m) },      // Roma, Italia
            { "DE", (52.5200m, 13.4050m) },      // Berlín, Alemania
            { "GB", (51.5074m, -0.1278m) },      // Londres, Reino Unido
            { "CA", (45.4215m, -75.6972m) },     // Ottawa, Canadá
            { "AU", (-35.2809m, 149.1300m) },    // Canberra, Australia
            { "JP", (35.6762m, 139.6503m) },     // Tokio, Japón
            { "CN", (39.9042m, 116.4074m) },     // Pekín, China
            { "IN", (28.6139m, 77.2090m) },      // Nueva Delhi, India
        };

        /// <summary>
        /// Coordenadas de la capital del país (público para endpoints ligeros de geolocalización por IP).
        /// </summary>
        public (decimal Latitude, decimal Longitude) GetCountryCapitalCoordinates(string? countryCode)
        {
            return GetCapitalCoordinates(countryCode);
        }

        /// <summary>
        /// Obtiene las coordenadas de la capital de un país
        /// </summary>
        private (decimal Latitude, decimal Longitude) GetCapitalCoordinates(string? countryCode)
        {
            // Si no se proporciona código de país o no está en el diccionario, usar Madrid por defecto
            if (string.IsNullOrWhiteSpace(countryCode) || !CapitalCoordinates.TryGetValue(countryCode.ToUpperInvariant(), out var coords))
            {
                return CapitalCoordinates["ES"]; // Madrid por defecto
            }
            return coords;
        }

        /// <summary>
        /// Obtiene servicios cercanos a una ubicación. Si no se proporciona ubicación, usa la capital del país.
        /// ✅ Filtra por categoría si se proporciona categoryId.
        /// </summary>
        public async Task<(IEnumerable<SearchServiceHomepageDto> services, int totalCount)> GetNearbyServices(
            string? latitude,
            string? longitude,
            string? countryCode,
            int locationRange,
            int? categoryId = null,  // ✅ Filtro por categoría
            int page = 1,
            int pageSize = 20,
            CancellationToken cancellationToken = default)
        {
            var methodStartTime = DateTime.UtcNow;
            _logger.LogInformation($"[SERVICE] ========================================");
            _logger.LogInformation($"[SERVICE] 📥 GetNearbyServices INICIADO");
            _logger.LogInformation($"[SERVICE]    latitude: {latitude}, longitude: {longitude}");
            _logger.LogInformation($"[SERVICE]    countryCode: {countryCode}, locationRange: {locationRange}");
            _logger.LogInformation($"[SERVICE]    categoryId: {categoryId}, page: {page}, pageSize: {pageSize}");
            
            try
            {
                // Validar parámetros de paginación
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;
                if (locationRange <= 0) locationRange = 50; // Rango por defecto de 50km

                decimal searchLatitude;
                decimal searchLongitude;

                // Si no se proporciona ubicación, usar la capital del país (o Madrid por defecto)
                if (string.IsNullOrWhiteSpace(latitude) || string.IsNullOrWhiteSpace(longitude))
                {
                    _logger.LogInformation($"[SERVICE] 🔍 GetNearbyServices - No hay coordenadas, usando capital del país");
                    var capitalCoords = GetCapitalCoordinates(countryCode);
                    searchLatitude = capitalCoords.Latitude;
                    searchLongitude = capitalCoords.Longitude;
                    _logger.LogInformation($"[SERVICE] ✅ GetNearbyServices - Coordenadas capital: {searchLatitude}, {searchLongitude}");
                }
                else
                {
                    // Validar que las coordenadas sean válidas
                    if (!decimal.TryParse(latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out searchLatitude) ||
                        !decimal.TryParse(longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out searchLongitude))
                    {
                        _logger.LogInformation($"[SERVICE] ⚠️ GetNearbyServices - Coordenadas inválidas, usando capital");
                        // Si las coordenadas no son válidas, usar capital
                        var capitalCoords = GetCapitalCoordinates(countryCode);
                        searchLatitude = capitalCoords.Latitude;
                        searchLongitude = capitalCoords.Longitude;
                    }
                    else
                    {
                        _logger.LogInformation($"[SERVICE] ✅ GetNearbyServices - Coordenadas válidas: {searchLatitude}, {searchLongitude}");
                    }
                }

                // ✅ OPTIMIZACIÓN CRÍTICA: Primero obtener IDs y distancias (sin cargar todas las relaciones)
                // Esto es 100x más rápido porque solo carga IDs y coordenadas
                _logger.LogInformation($"[SERVICE] 🔍 GetNearbyServices - Construyendo query para obtener IDs y coordenadas...");
                var queryStartTime = DateTime.UtcNow;
                var servicesWithDistanceQuery = _context.SearchServices
                    .AsNoTracking()
                    .Where(ss => ss.IsActive 
                        && !ss.ExpertProfile.IsOnVacation
                        && (ss.ExpertProfile.StripeStatus == StripeStatus.Approved && ss.ExpertProfile.OnboardingCompleted
                            || ss.ExpertProfile.StripeStatus == StripeStatus.PendingVerification)
                        // 🧩 PERFIL COMPLETO (Stripe-first): invisible hasta rellenar foto, descripción y ubicación.
                        && ss.ExpertProfile.Description != null && ss.ExpertProfile.Description != ""
                        && ss.ExpertProfile.ProfilePictureUrl != null && ss.ExpertProfile.ProfilePictureUrl != ""
                        // 📱 OBLIGATORIO: móvil verificado (no fijo) para ser visible — los avisos van por SMS.
                        && ss.ExpertProfile.User.PhoneVerified
                        && (ss.ExpertProfile.User.PhoneLineType == null || ss.ExpertProfile.User.PhoneLineType != "landline")
                        && !string.IsNullOrEmpty(ss.ExpertProfile.Latitude) 
                        && !string.IsNullOrEmpty(ss.ExpertProfile.Longitude)
                        && (categoryId == null || ss.CategoryId == categoryId)  // ✅ FILTRO POR CATEGORÍA
                        && (string.IsNullOrWhiteSpace(countryCode) || ss.ExpertProfile.Country == countryCode))  // ✅ FILTRO POR PAÍS
                    .Select(ss => new
                    {
                        ServiceId = ss.Id,
                        Latitude = ss.ExpertProfile.Latitude,
                        Longitude = ss.ExpertProfile.Longitude,
                        WorkRadiusKm = ss.ExpertProfile.WorkRadiusKm
                    });

                _logger.LogInformation($"[SERVICE] ✅ GetNearbyServices - Query construida, ejecutando ToListAsync... (Duración construcción: {(DateTime.UtcNow - queryStartTime).TotalMilliseconds:F2}ms)");
                var dbQueryStartTime = DateTime.UtcNow;
                var servicesWithDistanceData = await servicesWithDistanceQuery.ToListAsync(cancellationToken);
                var dbQueryDuration = (DateTime.UtcNow - dbQueryStartTime).TotalMilliseconds;
                _logger.LogInformation($"[SERVICE] ✅ GetNearbyServices - Query completada: {servicesWithDistanceData.Count} servicios obtenidos, Duración: {dbQueryDuration:F2}ms");

                // Calcular distancia en memoria (solo IDs y coordenadas, muy rápido)
                _logger.LogInformation($"[SERVICE] 🔍 GetNearbyServices - Calculando distancias en memoria...");
                var distanceCalcStartTime = DateTime.UtcNow;
                var servicesWithDistance = servicesWithDistanceData
                    .Select(ss =>
                    {
                        if (string.IsNullOrEmpty(ss.Latitude) || string.IsNullOrEmpty(ss.Longitude) ||
                            !decimal.TryParse(ss.Latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLat) ||
                            !decimal.TryParse(ss.Longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var expertLng))
                        {
                            return null;
                        }

                        var distance = CalculateDistance(searchLatitude, searchLongitude, expertLat, expertLng);
                        return new { ServiceId = ss.ServiceId, Distance = distance, WorkRadiusKm = ss.WorkRadiusKm };
                    })
                    .Where(x => x != null)
                    .OrderBy(x => x!.Distance)
                    .ToList();
                var distanceCalcDuration = (DateTime.UtcNow - distanceCalcStartTime).TotalMilliseconds;
                _logger.LogInformation($"[SERVICE] ✅ GetNearbyServices - Distancias calculadas: {servicesWithDistance.Count} servicios, Duración: {distanceCalcDuration:F2}ms");

                // ✅ LÓGICA MEJORADA: Si se usa countryCode sin coordenadas específicas (homepage-wall),
                // devolver TODOS los servicios del país ordenados por distancia, no solo los del rango
                // Esto es porque el usuario quiere ver servicios de todo el país, no solo de la capital
                bool isCountryWideSearch = string.IsNullOrWhiteSpace(latitude) && string.IsNullOrWhiteSpace(longitude) && !string.IsNullOrWhiteSpace(countryCode);
                
                // ✅ WORK RADIUS: además del rango del cliente, respetar el radio de trabajo del
                // experto. WorkRadiusKm == 0 significa "solo en su taller" (el cliente se desplaza),
                // así que NO se excluye por distancia; con radio > 0 el experto solo aparece "en
                // rango" si la distancia no supera lo que él está dispuesto a viajar.
                var servicesInRange = servicesWithDistance
                    .Where(x => x!.Distance <= locationRange
                                && (x.WorkRadiusKm == 0 || x.Distance <= x.WorkRadiusKm))
                    .ToList();
                
                var servicesToUse = isCountryWideSearch
                    ? servicesWithDistance // Para búsquedas por país, devolver TODOS ordenados por distancia
                    : servicesInRange.Count > 0 
                        ? servicesInRange // Si hay en rango, usar esos
                    : servicesWithDistance; // Si no hay en rango, usar todos ordenados por distancia
                
                if (isCountryWideSearch)
                {
                    _logger.LogInformation($"[SERVICE] 🌍 GetNearbyServices - Búsqueda por país ({countryCode}), devolviendo TODOS los servicios ordenados por distancia: {servicesToUse.Count}");
                }
                else
                {
                    _logger.LogInformation($"[SERVICE] 🔍 GetNearbyServices - Servicios en rango ({locationRange}km): {servicesInRange.Count}");
                }

                var totalCount = servicesToUse.Count;
                _logger.LogInformation($"[SERVICE] ✅ GetNearbyServices - Total servicios a usar: {totalCount}");

                // ✅ OPTIMIZACIÓN: Aplicar paginación ANTES de cargar los datos completos
                var paginatedServiceIds = servicesToUse
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(x => x!.ServiceId)
                    .ToList();

                _logger.LogInformation($"[SERVICE] 🔍 GetNearbyServices - IDs paginados: {paginatedServiceIds.Count} servicios (página {page}, tamaño {pageSize})");

                if (paginatedServiceIds.Count == 0)
                {
                    _logger.LogInformation($"[SERVICE] ✅ GetNearbyServices COMPLETADO - Sin servicios, retornando lista vacía");
                    _logger.LogInformation($"[SERVICE]    Duración total: {(DateTime.UtcNow - methodStartTime).TotalMilliseconds:F2}ms");
                    _logger.LogInformation($"[SERVICE] ========================================");
                    return (new List<SearchServiceHomepageDto>(), 0);
                }

                // ✅ OPTIMIZACIÓN HOMEPAGE: Usar proyección Select en lugar de Include - MUCHO más rápido
                // Solo carga los campos necesarios para mostrar cards en homepage, no todas las relaciones
                // ✅ CRÍTICO: Agregar timeout corto para evitar bloqueos de 90+ segundos
                _logger.LogInformation($"[SERVICE] 🔍 GetNearbyServices - Ejecutando query para obtener datos completos de servicios...");
                var homepageQueryStartTime = DateTime.UtcNow;
                
                // ✅ DESARROLLO: Timeout aumentado a 60 segundos para desarrollo (PC lento)
                using var queryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                queryCts.CancelAfter(TimeSpan.FromSeconds(60)); // Timeout de 60 segundos para desarrollo
                
                var homepageServicesQuery = _context.SearchServices
                        .AsNoTracking()
                        .Where(ss => paginatedServiceIds.Contains(ss.Id))
                        .Select(ss => new
                        {
                            ServiceId = ss.Id,
                            CategoryId = ss.CategoryId,
                            CategoryName = ss.Category.Name,
                            ServiceTypeId = ss.ServiceTypeId,
                            ServiceTypeName = ss.ServiceType.Name,
                            Price = ss.Price,
                            // Round 24: currency original del precio.
                            Currency = ss.Currency,
                            // Solo primeras 2 imágenes (suficiente para homepage) - Cargar datos sin procesar URLs
                            Images = ss.Images
                                .OrderBy(img => img.Id)
                                .Take(2)
                                .Select(img => new
                                {
                                    ImageUrl = img.ImageUrl,
                                    ImageObjectName = img.ImageObjectName
                                })
                                .ToList(),
                        ExpertId = ss.ExpertProfile.Id,
                        ExpertName = ss.ExpertProfile.User.Name,
                        ExpertProfilePictureUrl = ss.ExpertProfile.ProfilePictureUrl,
                        ExpertProfilePictureObjectName = ss.ExpertProfile.ProfilePictureObjectName,
                        ExpertCountry = ss.ExpertProfile.Country,
                        ExpertCity = ss.ExpertProfile.City,
                        ExpertWorkRadiusKm = ss.ExpertProfile.WorkRadiusKm,
                        // ✅ OPTIMIZACIÓN: Simplificar cálculo de rating - evitar Any() y Average() que son lentos
                        // En lugar de calcular en SQL, usar un campo calculado o query separada
                        // Por ahora retornar 0 y se puede optimizar después
                        AverageRating = 0.0, // TODO: Optimizar con campo calculado o query separada
                        // ✅ OPTIMIZACIÓN: Remover Count pesado - se puede calcular después si es necesario
                        // El Count de SearchHiresAsExpert es muy lento (puede tener miles de registros)
                        // Por ahora retornar 0 y se puede optimizar después con un campo calculado
                        CompletedSearches = 0 // TODO: Optimizar con campo calculado o query separada
                    });
                
                var homepageServices = await homepageServicesQuery.ToListAsync(queryCts.Token);
                var homepageQueryDuration = (DateTime.UtcNow - homepageQueryStartTime).TotalMilliseconds;
                _logger.LogInformation($"[SERVICE] ✅ GetNearbyServices - Query de datos completos completada: {homepageServices.Count} servicios, Duración: {homepageQueryDuration:F2}ms");

                // Mantener el orden original
                var orderedServices = homepageServices
                    .OrderBy(s => paginatedServiceIds.IndexOf(s.ServiceId))
                    .ToList();

                // ✅ OPTIMIZACIÓN: Cargar disponibilidades de todos los expertos de una vez
                // ✅ CRÍTICO: Usar timeout corto para evitar bloqueos - si falla, continuar sin disponibilidades
                var expertIds = orderedServices.Select(s => s.ExpertId).Distinct().ToList();
                _logger.LogInformation($"[SERVICE] 🔍 GetNearbyServices - Obteniendo disponibilidades para {expertIds.Count} expertos...");
                _logger.LogInformation($"[SERVICE]    Expert IDs: [{string.Join(", ", expertIds)}]");
                var availabilityQueryStartTime = DateTime.UtcNow;
                
                List<ExpertAvailability> availabilities = new List<ExpertAvailability>();
                
                // ✅ TIMEOUT: Si no hay expertos, saltar la consulta
                if (expertIds.Count > 0)
                {
                    // ✅ CRÍTICO: El CommandTimeout de la connection string (120s) tiene prioridad sobre CancellationToken
                    // ✅ SOLUCIÓN: Establecer timeout temporalmente en el DbContext antes de la query
                    var originalTimeout = _context.Database.GetCommandTimeout();
                    
                    try
                    {
                        // ✅ TIMEOUT: Usar un CancellationTokenSource con timeout de 5 segundos para esta consulta específica
                        using var availabilityCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        availabilityCts.CancelAfter(TimeSpan.FromSeconds(5)); // Timeout de 5 segundos
                        
                        _context.Database.SetCommandTimeout(5); // ✅ Forzar timeout de 5 segundos para esta query específica
                        
                        _logger.LogInformation($"[SERVICE]    Ejecutando consulta de disponibilidades con timeout de 5 segundos...");
                        availabilities = await _context.ExpertAvailabilities
                            .AsNoTracking()
                            .Where(ea => expertIds.Contains(ea.ExpertId) && ea.IsActive && ea.EffectiveTo == null)
                            .ToListAsync(availabilityCts.Token);
                        
                        var availabilityQueryDuration = (DateTime.UtcNow - availabilityQueryStartTime).TotalMilliseconds;
                        _logger.LogInformation($"[SERVICE] ✅ GetNearbyServices - Disponibilidades obtenidas: {availabilities.Count}, Duración: {availabilityQueryDuration:F2}ms");
                    }
                    catch (OperationCanceledException)
                    {
                        var availabilityQueryDuration = (DateTime.UtcNow - availabilityQueryStartTime).TotalMilliseconds;
                        _logger.LogWarning($"[SERVICE] ⚠️ GetNearbyServices - Timeout obteniendo disponibilidades después de {availabilityQueryDuration:F2}ms - Continuando sin disponibilidades");
                        availabilities = new List<ExpertAvailability>();
                    }
                    catch (Exception ex)
                    {
                        var availabilityQueryDuration = (DateTime.UtcNow - availabilityQueryStartTime).TotalMilliseconds;
                        _logger.LogError($"[SERVICE] ❌ GetNearbyServices - ERROR obteniendo disponibilidades después de {availabilityQueryDuration:F2}ms");
                        _logger.LogError($"[SERVICE]    Exception: {ex.GetType().Name} - {ex.Message}");
                        _logger.LogError($"[SERVICE]    Inner Exception: {ex.InnerException?.Message ?? "None"}");
                        _logger.LogError($"[SERVICE]    StackTrace: {ex.StackTrace}");
                        // Continuar sin disponibilidades en caso de error
                        availabilities = new List<ExpertAvailability>();
                    }
                    finally
                    {
                        // ✅ CRÍTICO: Siempre restaurar timeout original, incluso si hay error
                        _context.Database.SetCommandTimeout(originalTimeout);
                    }
                }
                else
                {
                    _logger.LogInformation($"[SERVICE] ⚠️ GetNearbyServices - No hay expert IDs, saltando consulta de disponibilidades");
                }
                
                _logger.LogInformation($"[SERVICE] 🔍 GetNearbyServices - Agrupando disponibilidades por experto...");
                var groupingStartTime = DateTime.UtcNow;
                var availabilityByExpert = availabilities
                    .GroupBy(ea => ea.ExpertId)
                    .ToDictionary(g => g.Key, g => g.First());
                var groupingDuration = (DateTime.UtcNow - groupingStartTime).TotalMilliseconds;
                _logger.LogInformation($"[SERVICE] ✅ GetNearbyServices - Disponibilidades agrupadas, Duración: {groupingDuration:F2}ms");

                // Mapear a DTO ligero - Aplicar lógica de URLs firmadas en memoria (después de cargar datos)
                _logger.LogInformation($"[SERVICE] 🔍 GetNearbyServices - Mapeando servicios a DTOs...");
                var mappingStartTime = DateTime.UtcNow;
                var mappedServices = orderedServices.Select(s => 
                {
                    // Obtener disponibilidad del experto si existe
                    HomepageExpertAvailabilityDto? availabilityDto = null;
                    if (availabilityByExpert.TryGetValue(s.ExpertId, out var availability))
                    {
                        var daysOfWeek = System.Text.Json.JsonSerializer.Deserialize<List<string>>(availability.DaysOfWeek) ?? new List<string>();
                        availabilityDto = new HomepageExpertAvailabilityDto
                        {
                            DaysOfWeek = daysOfWeek,
                            StartTime = availability.StartTime,
                            EndTime = availability.EndTime
                        };
                    }

                    return new SearchServiceHomepageDto
                    {
                        Id = s.ServiceId,
                        CategoryId = s.CategoryId,
                        CategoryName = s.CategoryName,
                        ServiceTypeId = s.ServiceTypeId,
                        ServiceTypeName = s.ServiceTypeName,
                        Price = s.Price,
                        // Round 24: poblar Currency (default 'EUR' si null).
                        Currency = string.IsNullOrEmpty(s.Currency) ? "EUR" : s.Currency,
                        // Procesar URLs de imágenes en memoria (no en SQL)
                        ImageUrls = s.Images
                            .Select(img =>
                            {
                                if (!string.IsNullOrWhiteSpace(img.ImageUrl) &&
                                    (!img.ImageUrl.Contains("storage.googleapis.com") || string.IsNullOrWhiteSpace(_configuration["GoogleCloud:BucketName"])))
                                {
                                    return img.ImageUrl;
                                }
                                return _signedUrlService.GetSignedUrl(img.ImageObjectName ?? string.Empty) ?? img.ImageUrl;
                            })
                            .Where(url => !string.IsNullOrEmpty(url))
                            .ToList(),
                        Expert = new HomepageExpertDto
                        {
                            Id = s.ExpertId,
                            Name = s.ExpertName,
                            // Procesar URL de foto de perfil en memoria (no en SQL)
                            ProfilePictureUrl = !string.IsNullOrWhiteSpace(s.ExpertProfilePictureObjectName)
                                ? _signedUrlService.GetSignedUrl(s.ExpertProfilePictureObjectName) ?? s.ExpertProfilePictureUrl ?? string.Empty
                                : s.ExpertProfilePictureUrl ?? string.Empty,
                            Country = s.ExpertCountry,
                            City = s.ExpertCity,
                            WorkRadiusKm = s.ExpertWorkRadiusKm,
                            Availability = availabilityDto
                        },
                        AverageRating = s.AverageRating,
                        CompletedSearches = s.CompletedSearches
                    };
                }).ToList();
                var mappingDuration = (DateTime.UtcNow - mappingStartTime).TotalMilliseconds;
                _logger.LogInformation($"[SERVICE] ✅ GetNearbyServices - Mapeo completado: {mappedServices.Count} servicios mapeados, Duración: {mappingDuration:F2}ms");

                _logger.LogInformation($"[SERVICE] ✅ GetNearbyServices COMPLETADO");
                _logger.LogInformation($"[SERVICE]    Total servicios retornados: {mappedServices.Count}");
                _logger.LogInformation($"[SERVICE]    Total count: {totalCount}");
                _logger.LogInformation($"[SERVICE]    Duración total: {(DateTime.UtcNow - methodStartTime).TotalMilliseconds:F2}ms");
                _logger.LogInformation($"[SERVICE] ========================================");
                return (mappedServices, totalCount);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[SERVICE] ❌ GetNearbyServices ERROR");
                _logger.LogError($"[SERVICE]    Exception Type: {ex.GetType().Name}");
                _logger.LogError($"[SERVICE]    Exception Message: {ex.Message}");
                _logger.LogError($"[SERVICE]    Inner Exception: {ex.InnerException?.Message ?? "None"}");
                _logger.LogError($"[SERVICE]    StackTrace: {ex.StackTrace}");
                _logger.LogError($"[SERVICE]    Duración antes del error: {(DateTime.UtcNow - methodStartTime).TotalMilliseconds:F2}ms");
                _logger.LogError($"[SERVICE] ========================================");
                throw;
            }
        }

        /// <summary>
        /// Obtiene servicios populares ordenados por rating y número de contrataciones completadas
        /// </summary>
        public async Task<(IEnumerable<SearchServiceHomepageDto> services, int totalCount)> GetPopularServices(
            int? categoryId = null,  // ✅ Filtro por categoría
            int page = 1,
            int pageSize = 20,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Validar parámetros de paginación
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                // ✅ OPTIMIZACIÓN CRÍTICA: Primero obtener IDs y calcular popularidad en memoria (más rápido que SQL complejo)
                // Cargar solo IDs y datos necesarios para calcular popularidad
                var servicesWithPopularityData = await _context.SearchServices
                    .AsNoTracking()
                    .Where(ss => ss.IsActive 
                        && !ss.ExpertProfile.IsOnVacation
                        && (ss.ExpertProfile.StripeStatus == StripeStatus.Approved && ss.ExpertProfile.OnboardingCompleted
                            || ss.ExpertProfile.StripeStatus == StripeStatus.PendingVerification)
                        // 🧩 PERFIL COMPLETO (Stripe-first): invisible hasta rellenar foto, descripción y ubicación.
                        && ss.ExpertProfile.Description != null && ss.ExpertProfile.Description != ""
                        && ss.ExpertProfile.ProfilePictureUrl != null && ss.ExpertProfile.ProfilePictureUrl != ""
                        // 📱 OBLIGATORIO: móvil verificado (no fijo) para ser visible — los avisos van por SMS.
                        && ss.ExpertProfile.User.PhoneVerified
                        && (ss.ExpertProfile.User.PhoneLineType == null || ss.ExpertProfile.User.PhoneLineType != "landline")
                        && ss.ExpertProfile.Latitude != null && ss.ExpertProfile.Latitude != ""
                        && (categoryId == null || ss.CategoryId == categoryId))  // ✅ FILTRO POR CATEGORÍA
                    .Select(ss => new
                    {
                        ServiceId = ss.Id,
                        Reviews = ss.ExpertProfile.User.ReviewsReceived.Select(r => r.Score).ToList(),
                        CompletedSearches = ss.ExpertProfile.User.SearchHiresAsExpert
                            .Count(sh => sh.Status != null && sh.Status.StatusValue == "completed")
                    })
                    .ToListAsync(cancellationToken);

                // Calcular popularidad en memoria (muy rápido con datos mínimos)
                var servicesWithPopularity = servicesWithPopularityData
                    .Select(x =>
                    {
                        var averageRating = x.Reviews.Any() ? (decimal)x.Reviews.Average() : 0m;
                        var popularityScore = (averageRating * 0.6m) + (Math.Min(x.CompletedSearches, 100) / 100m * 0.4m);
                        return new
                        {
                            ServiceId = x.ServiceId,
                            PopularityScore = popularityScore,
                            AverageRating = averageRating,
                            CompletedSearches = x.CompletedSearches
                        };
                    })
                    .OrderByDescending(x => x.PopularityScore)
                    .ThenByDescending(x => x.CompletedSearches)
                    .ToList();

                var totalCount = servicesWithPopularity.Count;

                // ✅ OPTIMIZACIÓN: Aplicar paginación ANTES de cargar los datos completos
                var paginatedPopularityData = servicesWithPopularity
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();

                if (paginatedPopularityData.Count == 0)
                {
                    return (new List<SearchServiceHomepageDto>(), 0);
                }

                var paginatedServiceIds = paginatedPopularityData.Select(x => x.ServiceId).ToList();

                // ✅ OPTIMIZACIÓN HOMEPAGE: Usar proyección Select en lugar de Include
                var homepageServices = await _context.SearchServices
                    .AsNoTracking()
                    .Where(ss => paginatedServiceIds.Contains(ss.Id))
                    .Select(ss => new
                    {
                        ServiceId = ss.Id,
                        CategoryId = ss.CategoryId,
                        CategoryName = ss.Category.Name,
                        ServiceTypeId = ss.ServiceTypeId,
                        ServiceTypeName = ss.ServiceType.Name,
                        Price = ss.Price,
                        // Round 24: currency original del precio.
                        Currency = ss.Currency,
                        // Solo primeras 2 imágenes (suficiente para homepage) - Cargar datos sin procesar URLs
                        Images = ss.Images
                            .OrderBy(img => img.Id)
                            .Take(2)
                            .Select(img => new
                            {
                                ImageUrl = img.ImageUrl,
                                ImageObjectName = img.ImageObjectName
                            })
                            .ToList(),
                        ExpertId = ss.ExpertProfile.Id,
                        ExpertName = ss.ExpertProfile.User.Name,
                        ExpertProfilePictureUrl = ss.ExpertProfile.ProfilePictureUrl,
                        ExpertProfilePictureObjectName = ss.ExpertProfile.ProfilePictureObjectName,
                        ExpertCountry = ss.ExpertProfile.Country,
                        ExpertCity = ss.ExpertProfile.City,
                        ExpertWorkRadiusKm = ss.ExpertProfile.WorkRadiusKm,
                        AverageRating = ss.ExpertProfile.User.ReviewsReceived.Any()
                            ? ss.ExpertProfile.User.ReviewsReceived.Average(r => (double)r.Score)
                            : 0.0,
                        CompletedSearches = ss.ExpertProfile.User.SearchHiresAsExpert
                            .Count(sh => sh.Status != null && sh.Status.StatusValue == "completed")
                    })
                    .ToListAsync(cancellationToken);

                // Mantener el orden original
                var orderedServices = homepageServices
                    .OrderBy(s => paginatedServiceIds.IndexOf(s.ServiceId))
                    .ToList();

                // ✅ OPTIMIZACIÓN: Cargar disponibilidades de todos los expertos de una vez
                // ✅ CRÍTICO: Usar timeout corto para evitar bloqueos - si falla, continuar sin disponibilidades
                var expertIds = orderedServices.Select(s => s.ExpertId).Distinct().ToList();
                
                List<ExpertAvailability> availabilities = new List<ExpertAvailability>();
                
                // ✅ TIMEOUT: Si no hay expertos, saltar la consulta
                if (expertIds.Count > 0)
                {
                    try
                    {
                        // ✅ TIMEOUT: Usar un CancellationTokenSource con timeout de 5 segundos para esta consulta específica
                        using var availabilityCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        availabilityCts.CancelAfter(TimeSpan.FromSeconds(5)); // Timeout de 5 segundos
                        
                        availabilities = await _context.ExpertAvailabilities
                            .AsNoTracking()
                            .Where(ea => expertIds.Contains(ea.ExpertId) && ea.IsActive && ea.EffectiveTo == null)
                            .ToListAsync(availabilityCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Continuar sin disponibilidades en caso de timeout
                        availabilities = new List<ExpertAvailability>();
                    }
                    catch (Exception)
                    {
                        // Continuar sin disponibilidades en caso de error
                        availabilities = new List<ExpertAvailability>();
                    }
                }
                
                var availabilityByExpert = availabilities
                    .GroupBy(ea => ea.ExpertId)
                    .ToDictionary(g => g.Key, g => g.First());

                // Mapear a DTO ligero - Aplicar lógica de URLs firmadas en memoria (después de cargar datos)
                var mappedServices = orderedServices.Select(s => 
                {
                    // Obtener disponibilidad del experto si existe
                    HomepageExpertAvailabilityDto? availabilityDto = null;
                    if (availabilityByExpert.TryGetValue(s.ExpertId, out var availability))
                    {
                        var daysOfWeek = System.Text.Json.JsonSerializer.Deserialize<List<string>>(availability.DaysOfWeek) ?? new List<string>();
                        availabilityDto = new HomepageExpertAvailabilityDto
                        {
                            DaysOfWeek = daysOfWeek,
                            StartTime = availability.StartTime,
                            EndTime = availability.EndTime
                        };
                    }

                    return new SearchServiceHomepageDto
                    {
                        Id = s.ServiceId,
                        CategoryId = s.CategoryId,
                        CategoryName = s.CategoryName,
                        ServiceTypeId = s.ServiceTypeId,
                        ServiceTypeName = s.ServiceTypeName,
                        Price = s.Price,
                        // Round 24: poblar Currency (default 'EUR' si null).
                        Currency = string.IsNullOrEmpty(s.Currency) ? "EUR" : s.Currency,
                        // Procesar URLs de imágenes en memoria (no en SQL)
                        ImageUrls = s.Images
                            .Select(img =>
                            {
                                if (!string.IsNullOrWhiteSpace(img.ImageUrl) &&
                                    (!img.ImageUrl.Contains("storage.googleapis.com") || string.IsNullOrWhiteSpace(_configuration["GoogleCloud:BucketName"])))
                                {
                                    return img.ImageUrl;
                                }
                                return _signedUrlService.GetSignedUrl(img.ImageObjectName ?? string.Empty) ?? img.ImageUrl;
                            })
                            .Where(url => !string.IsNullOrEmpty(url))
                            .ToList(),
                        Expert = new HomepageExpertDto
                        {
                            Id = s.ExpertId,
                            Name = s.ExpertName,
                            // Procesar URL de foto de perfil en memoria (no en SQL)
                            ProfilePictureUrl = !string.IsNullOrWhiteSpace(s.ExpertProfilePictureObjectName)
                                ? _signedUrlService.GetSignedUrl(s.ExpertProfilePictureObjectName) ?? s.ExpertProfilePictureUrl ?? string.Empty
                                : s.ExpertProfilePictureUrl ?? string.Empty,
                            Country = s.ExpertCountry,
                            City = s.ExpertCity,
                            WorkRadiusKm = s.ExpertWorkRadiusKm,
                            Availability = availabilityDto
                        },
                        AverageRating = s.AverageRating,
                        CompletedSearches = s.CompletedSearches
                    };
                }).ToList();

                return (mappedServices, totalCount);
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        /// <summary>
        /// Obtiene servicios de revisión agrupados por categoría y país para secciones específicas del homepage
        /// Solo incluye servicios de "Revisión" o "Búsqueda + Revisión" (no solo búsqueda)
        /// Acepta cualquier categoría (no solo Coches y Motos)
        /// </summary>
        public async Task<Dictionary<string, (IEnumerable<SearchServiceHomepageDto> services, int totalCount, string categoryName, string country)>> GetServicesByCategoryAndCountry(
            int maxSections = 10,
            int servicesPerSection = 20,
            string[]? targetCategories = null,
            string[]? targetCountries = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // ✅ FILTROS ESPECÍFICOS:
                // 1. Solo servicios de REVISIÓN (ServiceTypeCategoryId = 1 "Búsqueda + Revisión" o 2 "Revisión")
                // 2. Categorías: Si no se especifican, usa todas las categorías con más servicios
                // 3. Países: Si no se especifican, usa países principales: DE, GB, ES, FR, IT, PT
                // 4. Solo servicios activos con expertos verificados

                var revisionServiceTypeCategoryIds = new[] { 1, 2 }; // "Búsqueda + Revisión" y "Revisión"
                
                // Si no se especifican categorías, obtener las más populares
                if (targetCategories == null || targetCategories.Length == 0)
                {
                    var popularCategories = await _context.SearchServices
                        .AsNoTracking()
                        .Where(ss => ss.IsActive)
                        .GroupBy(ss => ss.Category.Name)
                        .OrderByDescending(g => g.Count())
                        .Take(10)
                        .Select(g => g.Key)
                        .ToListAsync(cancellationToken);
                    targetCategories = popularCategories.ToArray();
                }

                // Si no se especifican países, usar países principales
                if (targetCountries == null || targetCountries.Length == 0)
                {
                    targetCountries = new[] { "DE", "GB", "ES", "FR", "IT", "PT", "US", "MX" };
                }

                // Obtener servicios de revisión con sus categorías y países
                var servicesData = await _context.SearchServices
                    .AsNoTracking()
                    .Where(ss => ss.IsActive 
                        && !ss.ExpertProfile.IsOnVacation
                        && (ss.ExpertProfile.StripeStatus == StripeStatus.Approved && ss.ExpertProfile.OnboardingCompleted
                            || ss.ExpertProfile.StripeStatus == StripeStatus.PendingVerification)
                        // 🧩 PERFIL COMPLETO (Stripe-first): invisible hasta rellenar foto, descripción y ubicación.
                        && ss.ExpertProfile.Description != null && ss.ExpertProfile.Description != ""
                        && ss.ExpertProfile.ProfilePictureUrl != null && ss.ExpertProfile.ProfilePictureUrl != ""
                        // 📱 OBLIGATORIO: móvil verificado (no fijo) para ser visible — los avisos van por SMS.
                        && ss.ExpertProfile.User.PhoneVerified
                        && (ss.ExpertProfile.User.PhoneLineType == null || ss.ExpertProfile.User.PhoneLineType != "landline")
                        && ss.ExpertProfile.Latitude != null && ss.ExpertProfile.Latitude != ""
                        && !string.IsNullOrEmpty(ss.ExpertProfile.Country)
                        && targetCategories.Contains(ss.Category.Name)
                        && targetCountries.Contains(ss.ExpertProfile.Country)
                        && ss.ServiceType.ServiceTypeCategoryId.HasValue
                        && revisionServiceTypeCategoryIds.Contains(ss.ServiceType.ServiceTypeCategoryId.Value))
                    .Select(ss => new
                    {
                        ServiceId = ss.Id,
                        CategoryId = ss.CategoryId,
                        CategoryName = ss.Category.Name,
                        Country = ss.ExpertProfile.Country
                    })
                    .ToListAsync(cancellationToken);

                // Agrupar por categoría y país, ordenar por cantidad de servicios (más servicios = más relevante)
                var groupedServices = servicesData
                    .GroupBy(ss => new { ss.CategoryId, ss.CategoryName, ss.Country })
                    .Where(g => g.Count() > 0)
                    .OrderByDescending(g => g.Count()) // Más servicios primero
                    .ThenBy(g => g.Key.CategoryName) // Luego ordenar por categoría
                    .ThenBy(g => g.Key.Country) // Luego por país
                    .Take(maxSections)
                    .ToList();

                var result = new Dictionary<string, (IEnumerable<SearchServiceHomepageDto> services, int totalCount, string categoryName, string country)>();

                foreach (var group in groupedServices)
                {
                    var categoryId = group.Key.CategoryId;
                    var country = group.Key.Country;
                    var categoryName = group.Key.CategoryName;
                    
                    // ✅ Clave más clara: "Coches_DE", "Motos_GB", etc.
                    var key = $"{categoryName}_{country}";

                    // Obtener IDs de servicios para esta categoría y país
                    var serviceIds = group.Select(g => g.ServiceId).Take(servicesPerSection).ToList();

                    if (serviceIds.Count == 0) continue;

                    // ✅ OPTIMIZACIÓN HOMEPAGE: Usar proyección Select en lugar de Include
                    var homepageServices = await _context.SearchServices
                        .AsNoTracking()
                        .Where(ss => serviceIds.Contains(ss.Id))
                        .Select(ss => new
                        {
                            ServiceId = ss.Id,
                            CategoryId = ss.CategoryId,
                            CategoryName = ss.Category.Name,
                            ServiceTypeId = ss.ServiceTypeId,
                            ServiceTypeName = ss.ServiceType.Name,
                            Price = ss.Price,
                            // Round 24: currency original del precio.
                            Currency = ss.Currency,
                            // Solo primeras 2 imágenes (suficiente para homepage) - Cargar datos sin procesar URLs
                            Images = ss.Images
                                .OrderBy(img => img.Id)
                                .Take(2)
                                .Select(img => new
                                {
                                    ImageUrl = img.ImageUrl,
                                    ImageObjectName = img.ImageObjectName
                                })
                                .ToList(),
                            ExpertId = ss.ExpertProfile.Id,
                            ExpertName = ss.ExpertProfile.User.Name,
                            ExpertProfilePictureUrl = ss.ExpertProfile.ProfilePictureUrl,
                            ExpertProfilePictureObjectName = ss.ExpertProfile.ProfilePictureObjectName,
                            ExpertCountry = ss.ExpertProfile.Country,
                            ExpertCity = ss.ExpertProfile.City,
                            ExpertWorkRadiusKm = ss.ExpertProfile.WorkRadiusKm,
                            AverageRating = ss.ExpertProfile.User.ReviewsReceived.Any()
                                ? ss.ExpertProfile.User.ReviewsReceived.Average(r => (double)r.Score)
                                : 0.0,
                            CompletedSearches = ss.ExpertProfile.User.SearchHiresAsExpert
                                .Count(sh => sh.Status != null && sh.Status.StatusValue == "completed")
                        })
                        .ToListAsync(cancellationToken);

                    // Mantener el orden original
                    var orderedServices = homepageServices
                        .OrderBy(s => serviceIds.IndexOf(s.ServiceId))
                        .ToList();

                    // ✅ OPTIMIZACIÓN: Cargar disponibilidades de todos los expertos de una vez
                    var expertIds = orderedServices.Select(s => s.ExpertId).Distinct().ToList();
                    var availabilities = await _context.ExpertAvailabilities
                        .AsNoTracking()
                        .Where(ea => expertIds.Contains(ea.ExpertId) && ea.IsActive && ea.EffectiveTo == null)
                        .ToListAsync(cancellationToken);
                    
                    var availabilityByExpert = availabilities
                        .GroupBy(ea => ea.ExpertId)
                        .ToDictionary(g => g.Key, g => g.First());

                    // Mapear a DTO ligero - Aplicar lógica de URLs firmadas en memoria (después de cargar datos)
                    var mappedServices = orderedServices.Select(s => 
                    {
                        // Obtener disponibilidad del experto si existe
                        HomepageExpertAvailabilityDto? availabilityDto = null;
                        if (availabilityByExpert.TryGetValue(s.ExpertId, out var availability))
                        {
                            var daysOfWeek = System.Text.Json.JsonSerializer.Deserialize<List<string>>(availability.DaysOfWeek) ?? new List<string>();
                            availabilityDto = new HomepageExpertAvailabilityDto
                            {
                                DaysOfWeek = daysOfWeek,
                                StartTime = availability.StartTime,
                                EndTime = availability.EndTime
                            };
                        }

                        return new SearchServiceHomepageDto
                        {
                            Id = s.ServiceId,
                            CategoryId = s.CategoryId,
                            CategoryName = s.CategoryName,
                            ServiceTypeId = s.ServiceTypeId,
                            ServiceTypeName = s.ServiceTypeName,
                            Price = s.Price,
                            // Round 24: poblar Currency (default 'EUR' si null).
                            Currency = string.IsNullOrEmpty(s.Currency) ? "EUR" : s.Currency,
                            // Procesar URLs de imágenes en memoria (no en SQL)
                            ImageUrls = s.Images
                                .Select(img =>
                                {
                                    if (!string.IsNullOrWhiteSpace(img.ImageUrl) &&
                                        (!img.ImageUrl.Contains("storage.googleapis.com") || string.IsNullOrWhiteSpace(_configuration["GoogleCloud:BucketName"])))
                                    {
                                        return img.ImageUrl;
                                    }
                                    return _signedUrlService.GetSignedUrl(img.ImageObjectName ?? string.Empty) ?? img.ImageUrl;
                                })
                                .Where(url => !string.IsNullOrEmpty(url))
                                .ToList(),
                            Expert = new HomepageExpertDto
                            {
                                Id = s.ExpertId,
                                Name = s.ExpertName,
                                // Procesar URL de foto de perfil en memoria (no en SQL)
                                ProfilePictureUrl = !string.IsNullOrWhiteSpace(s.ExpertProfilePictureObjectName)
                                    ? _signedUrlService.GetSignedUrl(s.ExpertProfilePictureObjectName) ?? s.ExpertProfilePictureUrl ?? string.Empty
                                    : s.ExpertProfilePictureUrl ?? string.Empty,
                                Country = s.ExpertCountry,
                                City = s.ExpertCity,
                                WorkRadiusKm = s.ExpertWorkRadiusKm,
                                Availability = availabilityDto
                            },
                            AverageRating = s.AverageRating,
                            CompletedSearches = s.CompletedSearches
                        };
                    }).ToList();

                    var totalCount = group.Count(); // Total de servicios disponibles para esta combinación

                    // ✅ Incluir información adicional para el frontend
                    result[key] = (mappedServices, totalCount, categoryName, country);
                }

                return result;
            }
            catch (Exception ex)
            {
                throw;
            }
        }

    }
}