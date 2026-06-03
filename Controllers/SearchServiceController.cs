using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.Common;
using newApi.Services;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using System.Security.Claims;
using newApi.DataLayer.Models;
using System.Threading;
using Microsoft.Extensions.Logging;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SearchServiceController : ControllerBase
    {
        private readonly SearchServiceService _searchServiceService;
        private readonly AppDbContext _context;
        private readonly ILoggingService _loggingService;
        private readonly ILogger<SearchServiceController> _logger;
        private readonly IFavoriteService _favoriteService;
        private readonly ITimezoneService _timezoneService;
        private readonly IMemoryCache _memoryCache;

        // ✅ CACHÉ: Países que deben tener caché habilitado
        private static readonly HashSet<string> CachedCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ES", // España
            "DE", // Alemania
            "FR", // Francia
            "PT", // Portugal
            "MA", // Marruecos
            "IT"  // Italia
        };

        // ✅ CACHÉ: Categorías que deben tener caché habilitado (por nombre)
        private static readonly HashSet<string> CachedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Coches",
            "Motos",
            "Inmobiliaria"
        };

        // ✅ CACHÉ: TTL de 5 minutos (300 segundos)
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(5);

        public SearchServiceController(
            SearchServiceService searchServiceService,
            AppDbContext context,
            ILoggingService loggingService,
            ILogger<SearchServiceController> logger,
            IFavoriteService favoriteService,
            ITimezoneService timezoneService,
            IMemoryCache memoryCache)
        {
            _searchServiceService = searchServiceService;
            _context = context;
            _loggingService = loggingService;
            _logger = logger;
            _favoriteService = favoriteService;
            _timezoneService = timezoneService;
            _memoryCache = memoryCache;
        }

        private string GetStripeStatusMessage(StripeStatus status)
        {
            return status switch
            {
                StripeStatus.NotRequested => "You haven't set up your Stripe account yet. Configure your payment account to create services.",
                StripeStatus.Pending => "Your Stripe Connect onboarding is still in progress. Finish onboarding before publishing services.",
                StripeStatus.ActionRequired => "Stripe needs additional information right now. Open your Stripe dashboard to complete the missing data.",
                StripeStatus.PendingVerification => "Stripe is verifying the information you already submitted. Please wait for approval.",
                StripeStatus.RequirementsDue => "Stripe marked requirements that will be due soon. Complete them to avoid restrictions.",
                StripeStatus.RequirementsPastDue => "Some requirements expired and payouts are blocked. Update your information on Stripe to continue.",
                StripeStatus.RestrictedSoon => "Stripe will restrict your account if you don't resolve the pending requirements immediately.",
                StripeStatus.Restricted => "Your Stripe account is currently restricted. Resolve the action items shown in the Stripe dashboard.",
                StripeStatus.Disabled => "Stripe disabled payments/payouts for your account. Contact Stripe support if needed.",
                StripeStatus.Approved => "Your Stripe account is approved and ready to receive payments.",
                StripeStatus.Rejected => "Your Stripe account application was rejected. Contact support before trying again.",
                StripeStatus.Deauthorized => "Your Stripe account was disconnected. Restart onboarding to reconnect it.",
                _ => "Unknown Stripe account status. Please contact support."
            };
        }

        /// <summary>
        /// Convierte código de país ISO a nombre legible en español
        /// </summary>
        private string GetCountryDisplayName(string countryCode)
        {
            return countryCode?.ToUpperInvariant() switch
            {
                "DE" => "Alemania",
                "GB" => "Reino Unido",
                "ES" => "España",
                "FR" => "Francia",
                "IT" => "Italia",
                "PT" => "Portugal",
                "US" => "Estados Unidos",
                "MX" => "México",
                "AR" => "Argentina",
                "CO" => "Colombia",
                "CL" => "Chile",
                "PE" => "Perú",
                _ => countryCode ?? "Desconocido"
            };
        }

        /// <summary>
        /// [OBSOLETO] Este endpoint está obsoleto. Usa GET /api/SearchService/map-experts con parámetros latitude, longitude y locationRange en su lugar.
        /// </summary>
        [Obsolete("Este endpoint está obsoleto. Usa GET /api/SearchService/map-experts con parámetros latitude, longitude y locationRange en su lugar.")]
        [HttpGet]
        public async Task<IActionResult> GetAllServices(
            [FromQuery] int categoryId,
            [FromQuery] int serviceTypeId,
            [FromQuery] string latitude,
            [FromQuery] string longitude,
            [FromQuery] int locationRange)
        {
            try
            {
                if (categoryId <= 0)
                {
                    return BadRequest(new { message = "El ID de categoría es requerido y debe ser mayor que 0" });
                }

                if (serviceTypeId <= 0)
                {
                    return BadRequest(new { message = "El tipo de servicio es requerido y debe ser mayor que 0" });
                }

                if (string.IsNullOrEmpty(latitude) || string.IsNullOrEmpty(longitude) || locationRange <= 0)
                {
                    return BadRequest(new { message = "Latitude, Longitude, y LocationRange son requeridos y deben ser válidos" });
                }

                // Redirigir internamente a map-experts con los mismos parámetros
                var services = await _searchServiceService.GetAllServices(categoryId, serviceTypeId, latitude, longitude, locationRange);
                return Ok(services);
            }
            catch (Exception ex)
            {
                await _loggingService.LogErrorAsync(
                    message: "Error al obtener servicios (endpoint obsoleto GetAllServices)",
                    details: $"Exception: {ex.Message}\nStackTrace: {ex.StackTrace}",
                    source: "SearchServiceController.GetAllServices",
                    relatedEntityType: "SearchServices",
                    additionalData: new
                    {
                        categoryId,
                        serviceTypeId,
                        latitude,
                        longitude,
                        locationRange,
                        innerException = ex.InnerException?.Message,
                        exceptionType = ex.GetType().Name
                    },
                    notifyUser: false
                );
                return StatusCode(500, new { message = "Failed to retrieve services", detail = ex.Message });
            }
        }

        /// <summary>
        /// Obtiene expertos para mostrar en el mapa con información básica o completa según los parámetros.
        /// - Sin parámetros de ubicación: devuelve ExpertMapResponseDto (información básica para el mapa)
        /// - Con parámetros de ubicación (latitude, longitude, locationRange): devuelve SearchServiceDetailDto[] (información completa como GetAllServices)
        /// Soporta búsqueda por bounds del mapa (como Airbnb) para cargar servicios dinámicamente al mover el mapa.
        /// </summary>
        /// <param name="categoryId">ID de la categoría</param>
        /// <param name="serviceTypeId">ID del tipo de servicio</param>
        /// <param name="latitude">Latitud del punto de búsqueda (opcional, si se proporciona devuelve información completa)</param>
        /// <param name="longitude">Longitud del punto de búsqueda (opcional, si se proporciona devuelve información completa)</param>
        /// <param name="locationRange">Rango de búsqueda en km (opcional, requerido si se proporciona latitude/longitude)</param>
        /// <param name="northeastLat">Latitud del punto noreste del mapa visible (opcional, para filtrado por bounds)</param>
        /// <param name="northeastLng">Longitud del punto noreste del mapa visible (opcional, para filtrado por bounds)</param>
        /// <param name="southwestLat">Latitud del punto suroeste del mapa visible (opcional, para filtrado por bounds)</param>
        /// <param name="southwestLng">Longitud del punto suroeste del mapa visible (opcional, para filtrado por bounds)</param>
        /// <param name="zoom">Nivel de zoom del mapa (opcional, afecta el límite de resultados)</param>
        /// <param name="limit">Límite máximo de resultados (default: 100)</param>
        /// <returns>ExpertMapResponseDto si no hay ubicación, o SearchServiceDetailDto[] si hay ubicación</returns>
        [HttpGet("map-experts")]
        public async Task<IActionResult> GetMapExperts(
            [FromQuery] int categoryId,
            [FromQuery] int serviceTypeId,
            [FromQuery] string? latitude = null,
            [FromQuery] string? longitude = null,
            [FromQuery] int? locationRange = null,
            [FromQuery] decimal? northeastLat = null,
            [FromQuery] decimal? northeastLng = null,
            [FromQuery] decimal? southwestLat = null,
            [FromQuery] decimal? southwestLng = null,
            [FromQuery] int? zoom = null,
            [FromQuery] int limit = 100)
        {
            // ✅ RENDER.COM: Timeout aumentado a 90 segundos para consultas complejas
            // La connection string tiene CommandTimeout=120, así que 90s es razonable
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                if (categoryId <= 0)
                {
                    return BadRequest(new { message = "El ID de categoría es requerido y debe ser mayor que 0" });
                }

                if (serviceTypeId <= 0)
                {
                    return BadRequest(new { message = "El tipo de servicio es requerido y debe ser mayor que 0" });
                }

                // Validar que si se proporciona un bound, se proporcionen todos
                bool hasPartialBounds = (northeastLat.HasValue || northeastLng.HasValue || 
                                        southwestLat.HasValue || southwestLng.HasValue) &&
                                       !(northeastLat.HasValue && northeastLng.HasValue && 
                                         southwestLat.HasValue && southwestLng.HasValue);
                
                if (hasPartialBounds)
                {
                    return BadRequest(new { message = "Si se proporcionan bounds, deben proporcionarse todos los parámetros: northeastLat, northeastLng, southwestLat, southwestLng" });
                }

                // ✅ NUEVO: Detectar si hay bounds del mapa (desplazamiento por el mapa)
                bool hasBounds = northeastLat.HasValue && northeastLng.HasValue && 
                                southwestLat.HasValue && southwestLng.HasValue;

                // ✅ Si se proporcionan parámetros de ubicación (latitude/longitude/locationRange), usar GetAllServices
                bool hasLocationParams = !string.IsNullOrWhiteSpace(latitude) && 
                                        !string.IsNullOrWhiteSpace(longitude) && 
                                        locationRange.HasValue && locationRange.Value > 0;

                if (hasLocationParams)
                {
                    // Validar locationRange
                    if (locationRange.Value <= 0)
                    {
                        return BadRequest(new { message = "LocationRange debe ser mayor que 0" });
                    }

                    // Obtener parámetros de paginación (opcionales)
                    var page = Request.Query.ContainsKey("page") && int.TryParse(Request.Query["page"], out var pageValue) ? pageValue : 1;
                    var pageSize = Request.Query.ContainsKey("pageSize") && int.TryParse(Request.Query["pageSize"], out var pageSizeValue) ? pageSizeValue : 50;

                    // Usar GetAllServices para devolver información completa con paginación
                    var (services, totalCount) = await _searchServiceService.GetAllServices(
                        categoryId, 
                        serviceTypeId, 
                        latitude, 
                        longitude, 
                        locationRange.Value,
                        page,
                        pageSize,
                        cts.Token);
                    
                    return Ok(new
                    {
                        services = services,
                        pagination = new
                        {
                            page = page,
                            pageSize = pageSize,
                            totalCount = totalCount,
                            totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                            hasNextPage = page * pageSize < totalCount,
                            hasPreviousPage = page > 1
                        }
                    });
                }

                // ✅ Si hay bounds (desplazamiento por el mapa), devolver información completa
                if (hasBounds)
                {
                    if (limit <= 0 || limit > 500)
                    {
                        return BadRequest(new { message = "El límite debe estar entre 1 y 500" });
                    }

                    // Obtener parámetros de paginación (opcionales)
                    var page = Request.Query.ContainsKey("page") && int.TryParse(Request.Query["page"], out var pageValue) ? pageValue : 1;
                    var pageSize = Request.Query.ContainsKey("pageSize") && int.TryParse(Request.Query["pageSize"], out var pageSizeValue) ? pageSizeValue : 50;

                    // Devolver información completa cuando se mueve el mapa con paginación
                    var (services, totalCount) = await _searchServiceService.GetMapExpertsWithDetails(
                        categoryId, 
                        serviceTypeId,
                        northeastLat.Value,
                        northeastLng.Value,
                        southwestLat.Value,
                        southwestLng.Value,
                        zoom,
                        limit,
                        page,
                        pageSize,
                        cts.Token);
                    
                    return Ok(new
                    {
                        services = services,
                        pagination = new
                        {
                            page = page,
                            pageSize = pageSize,
                            totalCount = totalCount,
                            totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                            hasNextPage = page * pageSize < totalCount,
                            hasPreviousPage = page > 1
                        }
                    });
                }

                // ✅ Si no hay bounds ni parámetros de ubicación, usar GetMapExperts (información básica para carga inicial)
                if (limit <= 0 || limit > 500)
                {
                    return BadRequest(new { message = "El límite debe estar entre 1 y 500" });
                }

                var experts = await _searchServiceService.GetMapExperts(
                    categoryId, 
                    serviceTypeId,
                    null, // northeastLat
                    null, // northeastLng
                    null, // southwestLat
                    null, // southwestLng
                    zoom,
                    limit,
                    cts.Token);
                return Ok(experts);
            }
            catch (OperationCanceledException)
            {
                // ✅ FIX: Fire-and-forget — el logging abre scope EF + 2 SaveChangesAsync
                //    y en path de timeout (BD ya saturada) AMPLIFICA el problema, retrasando
                //    aún más el 408 que devuelve al cliente.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _loggingService.LogErrorAsync(
                            message: "Timeout al obtener expertos del mapa",
                            details: "La operación excedió el tiempo máximo de espera (90 segundos)",
                            source: "SearchServiceController.GetMapExperts",
                            relatedEntityType: "MapExperts",
                            additionalData: new { categoryId, serviceTypeId },
                            notifyUser: false
                        );
                    }
                    catch { /* swallow — log no debe romper response */ }
                });
                // ✅ CRÍTICO: Asegurar headers CORS y Content-Type ANTES de devolver StatusCode
                Response.Headers["Access-Control-Allow-Origin"] = Request.Headers["Origin"].ToString();
                Response.Headers["Access-Control-Allow-Credentials"] = "true";
                Response.ContentType = "application/json";

                return StatusCode(408, new { message = "Request timeout. Please try again.", detail = "The request took too long to complete" });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                // ✅ FIX: Fire-and-forget para no bloquear el response 500 esperando al log.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _loggingService.LogErrorAsync(
                            message: "Error al obtener expertos del mapa",
                            details: $"Exception: {ex.Message}\nStackTrace: {ex.StackTrace}",
                            source: "SearchServiceController.GetMapExperts",
                            relatedEntityType: "MapExperts",
                            additionalData: new
                            {
                                categoryId,
                                serviceTypeId,
                                latitude,
                                longitude,
                                locationRange,
                                innerException = ex.InnerException?.Message,
                                exceptionType = ex.GetType().Name
                            },
                            notifyUser: false
                        );
                    }
                    catch { /* swallow */ }
                });
                return StatusCode(500, new { message = "Failed to retrieve map experts", detail = ex.Message });
            }
        }

        /// <summary>
        /// Obtiene marcadores ultra ligeros para el mapa (solo coordenadas + precio)
        /// Optimizado para carga inicial rápida - 10-50x más rápido que GetMapExperts
        /// Similar a cómo Airbnb/Google Maps cargan marcadores iniciales
        /// </summary>
        /// <param name="categoryId">ID de la categoría</param>
        /// <param name="serviceTypeId">ID del tipo de servicio</param>
        /// <param name="northeastLat">Latitud noreste del bounds (opcional)</param>
        /// <param name="northeastLng">Longitud noreste del bounds (opcional)</param>
        /// <param name="southwestLat">Latitud suroeste del bounds (opcional)</param>
        /// <param name="southwestLng">Longitud suroeste del bounds (opcional)</param>
        /// <param name="zoom">Nivel de zoom (opcional, afecta el límite)</param>
        /// <param name="limit">Límite máximo de marcadores (default: 500)</param>
        /// <returns>MapMarkersResponseDto con solo coordenadas y precios</returns>
        [HttpGet("map-markers")]
        public async Task<IActionResult> GetMapMarkers(
            [FromQuery] int categoryId,
            [FromQuery] int serviceTypeId,
            [FromQuery] decimal? northeastLat = null,
            [FromQuery] decimal? northeastLng = null,
            [FromQuery] decimal? southwestLat = null,
            [FromQuery] decimal? southwestLng = null,
            [FromQuery] int? zoom = null,
            [FromQuery] int limit = 500)
        {
            // ✅ RENDER.COM: Timeout aumentado a 90 segundos para consultas complejas
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                if (categoryId <= 0 || serviceTypeId <= 0)
                {
                    return BadRequest(new { message = "CategoryId and ServiceTypeId must be greater than 0" });
                }

                if (limit <= 0 || limit > 1000)
                {
                    return BadRequest(new { message = "Limit must be between 1 and 1000" });
                }

                var markers = await _searchServiceService.GetMapMarkers(
                    categoryId,
                    serviceTypeId,
                    northeastLat,
                    northeastLng,
                    southwestLat,
                    southwestLng,
                    zoom,
                    limit,
                    cts.Token);

                return Ok(markers);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(408, new { message = "Request timeout. Please try again." });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                await _loggingService.LogErrorAsync(
                    message: "Error al obtener marcadores del mapa",
                    details: $"Exception: {ex.Message}",
                    source: "SearchServiceController.GetMapMarkers",
                    relatedEntityType: "MapMarkers",
                    additionalData: new { categoryId, serviceTypeId },
                    notifyUser: false
                );
                return StatusCode(500, new { message = "Failed to retrieve map markers", detail = ex.Message });
            }
        }

        /// <summary>
        /// Obtiene información básica para el sidebar (primera imagen + info básica)
        /// Optimizado para mostrar cards sin cargar toda la información
        /// </summary>
        /// <param name="serviceIds">Array de IDs de servicios (separados por coma o como array)</param>
        /// <returns>MapSidebarResponseDto con información básica de los servicios</returns>
        [HttpGet("map-sidebar")]
        public async Task<IActionResult> GetMapSidebar(
            [FromQuery] string serviceIds)
        {
            // ✅ RENDER.COM: Timeout aumentado a 90 segundos para consultas complejas
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                if (string.IsNullOrWhiteSpace(serviceIds))
                {
                    return Ok(new MapSidebarResponseDto { Services = new List<MapSidebarServiceDto>() });
                }

                // Parsear serviceIds (puede venir como "1,2,3" o como array)
                int[] ids;
                if (serviceIds.Contains(','))
                {
                    ids = serviceIds.Split(',')
                        .Select(id => int.TryParse(id.Trim(), out var parsedId) ? parsedId : 0)
                        .Where(id => id > 0)
                        .ToArray();
                }
                else
                {
                    if (int.TryParse(serviceIds, out var singleId))
                    {
                        ids = new[] { singleId };
                    }
                    else
                    {
                        return BadRequest(new { message = "Invalid serviceIds format" });
                    }
                }

                if (ids.Length == 0)
                {
                    return Ok(new MapSidebarResponseDto { Services = new List<MapSidebarServiceDto>() });
                }

                // Limitar a máximo 50 servicios por request (para evitar sobrecarga)
                if (ids.Length > 50)
                {
                    ids = ids.Take(50).ToArray();
                }

                var sidebarData = await _searchServiceService.GetMapSidebar(ids, cts.Token);

                return Ok(sidebarData);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(408, new { message = "Request timeout. Please try again." });
            }
            catch (Exception ex)
            {
                await _loggingService.LogErrorAsync(
                    message: "Error al obtener información del sidebar",
                    details: $"Exception: {ex.Message}",
                    source: "SearchServiceController.GetMapSidebar",
                    relatedEntityType: "MapSidebar",
                    additionalData: new { serviceIds },
                    notifyUser: false
                );
                return StatusCode(500, new { message = "Failed to retrieve sidebar data", detail = ex.Message });
            }
        }

        [HttpGet("expert/{expertId}")]
        public async Task<IActionResult> GetExpertServices(int expertId, [FromQuery] int? serviceTypeId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            try
            {
                // Validar parámetros
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                var (services, totalCount) = await _searchServiceService.GetExpertServices(expertId, serviceTypeId, page, pageSize);
                
                return Ok(new
                {
                    services,
                    pagination = new
                    {
                        page,
                        pageSize,
                        totalCount,
                        totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                        hasNextPage = page * pageSize < totalCount,
                        hasPreviousPage = page > 1
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to retrieve expert services", detail = ex.Message });
            }
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetServiceById(int id)
        {
            try
            {
                var service = await _searchServiceService.GetServiceById(id);
                if (service == null)
                {
                    return NotFound(new { message = "Service not found" });
                }
                return Ok(service);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to retrieve service", detail = ex.Message });
            }
        }


        [HttpGet("GetServiceByHireId/{id}")]
        public async Task<IActionResult> GetServiceByHireId(int id)
        {
            try
            {
                var service = await _searchServiceService.GetServiceByHireId(id);
                if (service == null)
                {
                    return NotFound(new { message = "Service not found" });
                }
                return Ok(service);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to retrieve service", detail = ex.Message });
            }
        }


        [Authorize(Roles = "Expert")] // 🔐 SEGURIDAD: Solo expertos pueden crear servicios
        [HttpPost]
        public async Task<IActionResult> CreateSearchService([FromForm] CreateSearchServiceRequestDto request)
        {
            try
            {
                foreach (var key in Request.Form.Keys)
                {
                    var values = Request.Form[key];
                    if (key == "Images")
                    {
                        foreach (var file in Request.Form.Files)
                        {
                        }
                    }
                    else
                    {
                    }
                }
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Verificar que el experto haya completado el onboarding de Stripe
                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    return BadRequest(new { message = "Expert profile not found" });
                }

                // ✅ FIX: Permitir PendingVerification si charges_enabled: true
                // PendingVerification es informativo, no bloqueante si Stripe permite operar
                if (expertProfile.StripeStatus == StripeStatus.PendingVerification)
                {
                    // PendingVerification no bloquea si la cuenta puede operar
                    // Permitir crear servicios durante verificación
                }
                else if (expertProfile.StripeStatus != StripeStatus.Approved || !expertProfile.OnboardingCompleted)
                {
                    var statusMessage = GetStripeStatusMessage(expertProfile.StripeStatus);
                    return BadRequest(new { 
                        message = statusMessage,
                        stripeStatus = expertProfile.StripeStatus.ToString(),
                        requiresStripeSetup = expertProfile.StripeStatus == StripeStatus.NotRequested,
                        canRetry = expertProfile.StripeStatus == StripeStatus.Rejected || expertProfile.StripeStatus == StripeStatus.NotRequested
                    });
                }

                if (request.ServiceTypeId <= 0)
                {
                    return BadRequest(new { message = "El tipo de servicio es requerido" });
                }

                if (string.IsNullOrWhiteSpace(request.Conditions))
                {
                    return BadRequest(new { message = "El campo Condiciones es requerido" });
                }

                if (request.Price <= 0)
                {
                    return BadRequest(new { message = "El precio debe ser mayor que 0" });
                }

                if (request.DurationInHours <= 0)
                {
                    return BadRequest(new { message = "La duración debe ser mayor que 0" });
                }

                // 🛡️ Round 28 — CUR-1 FIX CRÍTICO: derivar la divisa del país del experto.
                // Antes (R28 original): solo derivaba si `request.Currency` venía vacío. PERO el DTO
                // `CreateSearchServiceRequestDto.Currency` tiene default "EUR" → el frontend que NO
                // envía el campo (caso real, lo confirmó el Agente 1 con query a BD: 188/188 servicios
                // en EUR incluso para expertos US) hace que `request.Currency == "EUR"` SIEMPRE y la
                // rama de derivación NUNCA se ejecuta → experto US creaba servicios en EUR → mismatch
                // con su Stripe Account USD → currency_mismatch en payouts.
                //
                // Nueva regla:
                //  1. Si el frontend mandó algo distinto de EUR (USD/GBP/CHF/etc.) → respetar y validar.
                //  2. Si el frontend mandó EUR (default) Y el país del experto NO deriva en EUR
                //     (US→USD, GB→GBP, CH→CHF, SE→SEK, etc.) → preferir SIEMPRE la divisa del país.
                //     (Esto es lo que estaba roto: trataba EUR-default como decisión explícita.)
                //  3. Si el frontend mandó EUR y el país deriva en EUR (ES/DE/FR/IT/PT/...) → EUR.
                var derivedCurrency = global::newApi.Common.StripeCurrencyMapping
                    .GetCurrencyForCountry(expertProfile.Country)
                    .ToUpperInvariant();
                var normalizedRequest = global::newApi.Common.SupportedCurrenciesList.Normalize(request.Currency);

                if (string.IsNullOrEmpty(normalizedRequest)
                    || (string.Equals(normalizedRequest, "EUR", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(derivedCurrency, "EUR", StringComparison.OrdinalIgnoreCase)))
                {
                    // Caso 1: vacío/inválido. Caso 2: default EUR del DTO con experto no-eurozona.
                    // En ambos casos preferimos la divisa derivada del país del experto.
                    request.Currency = derivedCurrency;
                }
                else
                {
                    // El frontend mandó una divisa explícita distinta del default — respetar.
                    request.Currency = normalizedRequest;
                }

                // ✅ VALIDACIÓN: Verificar que al menos un tipo de entregable esté seleccionado
                if (string.IsNullOrWhiteSpace(request.SelectedDeliverableTypes))
                {
                    return BadRequest(new { message = "Debe seleccionar al menos un tipo de archivo de entrega (PDF, Video, etc.)" });
                }

                try
                {
                    var deliverableTypeIds = System.Text.Json.JsonSerializer.Deserialize<int[]>(request.SelectedDeliverableTypes);
                    if (deliverableTypeIds == null || deliverableTypeIds.Length == 0)
                    {
                        return BadRequest(new { message = "Debe seleccionar al menos un tipo de archivo de entrega (PDF, Video, etc.)" });
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    return BadRequest(new { message = "El formato de tipos de entregables no es válido" });
                }

                var (success, service, imageUrls) = await _searchServiceService.CreateSearchService(userId, request);
                if (!success)
                {
                    // ✅ MEJORAR: Verificar el motivo específico del fallo para dar un mensaje más claro
                    // Verificar que el ExpertProfileId del request coincide con el del experto autenticado
                    if (expertProfile.Id != request.ExpertProfileId)
                    {
                        return BadRequest(new { message = "Expert profile ID does not match your profile" });
                    }

                    // Verificar si ya existe un servicio activo con la misma categoría PADRE Y el mismo tipo de servicio
                    // Permite múltiples servicios de la misma categoría padre si el ServiceTypeId es diferente
                    var selectedCategory = await _context.Categories
                        .Include(c => c.Parent)
                        .FirstOrDefaultAsync(c => c.Id == request.CategoryId);

                    if (selectedCategory == null)
                    {
                        return BadRequest(new { message = "La categoría seleccionada no existe" });
                    }

                    // Determinar la categoría padre: si es subcategoría usar ParentId, si es categoría padre usar su Id
                    int parentCategoryId = selectedCategory.ParentId ?? selectedCategory.Id;
                    string parentCategoryName = selectedCategory.Parent?.Name ?? selectedCategory.Name;

                    // Buscar servicios activos del experto con el mismo tipo de servicio
                    var existingServices = await _context.SearchServices
                        .Where(ss => ss.ExpertProfileId == request.ExpertProfileId 
                                && ss.ServiceTypeId == request.ServiceTypeId
                                && ss.IsActive == true)
                        .Include(ss => ss.Category)
                            .ThenInclude(c => c.Parent)
                        .Include(ss => ss.ServiceType)
                        .ToListAsync();

                    // Verificar si algún servicio existente tiene la misma categoría padre
                    var existingService = existingServices
                        .Where(ss =>
                        {
                            var existingCategory = ss.Category;
                            if (existingCategory == null) return false;
                            int existingParentCategoryId = existingCategory.ParentId ?? existingCategory.Id;
                            return existingParentCategoryId == parentCategoryId;
                        })
                        .FirstOrDefault();

                    if (existingService != null)
                    {
                        var existingCategoryName = existingService.Category?.Name ?? "desconocida";
                        var existingServiceTypeName = existingService.ServiceType?.Name ?? "desconocido";
                        return BadRequest(new { 
                            message = $"Ya tienes un servicio activo en la categoría '{parentCategoryName}' (subcategoría: '{existingCategoryName}') con el tipo de servicio '{existingServiceTypeName}'. " +
                                     "Solo puedes tener un servicio por combinación de categoría padre y tipo de servicio. " +
                                     "Puedes actualizar tu servicio existente, crear uno con otro tipo de servicio en la misma categoría padre, o crear uno en otra categoría padre.",
                            existingServiceId = existingService.Id,
                            parentCategoryName = parentCategoryName,
                            existingCategoryName = existingCategoryName,
                            serviceTypeName = existingServiceTypeName
                        });
                    }

                    return BadRequest(new { message = "Failed to create service, possibly due to invalid ServiceTypeId, ExpertProfileId, or CategoryId" });
                }

                // Cargar los SelectedDeliverableTypes para la respuesta
                var selectedDeliverableTypes = await _context.SearchServiceDeliverableTypes
                    .Where(ssdt => ssdt.SearchServiceId == service.Id)
                    .Include(ssdt => ssdt.DeliverableType)
                    .Select(ssdt => new
                    {
                        ssdt.Id,
                        ssdt.DeliverableTypeId,
                        ssdt.IsSelected,
                        DeliverableType = new
                        {
                            ssdt.DeliverableType.Id,
                            ssdt.DeliverableType.Name,
                            ssdt.DeliverableType.DisplayName,
                            ssdt.DeliverableType.Description
                        }
                    })
                    .ToListAsync();

                return Ok(new
                {
                    message = "Search service created successfully",
                    searchService = new
                    {
                        service.Id,
                        service.ExpertProfileId,
                        service.CategoryId,
                        service.ServiceTypeId,
                        ServiceTypeName = service.ServiceType?.Name,
                        service.Price,
                        service.Currency,
                        service.Conditions,
                        service.DurationInHours,
                        service.CreatedAt,
                        service.IsActive,
                        ImageUrls = imageUrls,
                        SelectedDeliverableTypes = selectedDeliverableTypes
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to create search service", detail = ex.Message });
            }
        }

        [Authorize(Roles = "Expert")] // 🔐 SEGURIDAD: Solo expertos pueden actualizar servicios
        [HttpPut]
        public async Task<IActionResult> UpdateSearchService([FromForm] UpdateSearchServiceRequestDto request)
        {
            try
            {
                foreach (var key in Request.Form.Keys)
                {
                    var values = Request.Form[key];
                    if (key == "Images")
                    {
                        foreach (var file in Request.Form.Files)
                        {
                        }
                    }
                    else
                    {
                    }
                }
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Verificar que el experto haya completado el onboarding de Stripe
                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    return BadRequest(new { message = "Expert profile not found" });
                }

                // ✅ FIX: Permitir PendingVerification si charges_enabled: true
                // PendingVerification es informativo, no bloqueante si Stripe permite operar
                if (expertProfile.StripeStatus == StripeStatus.PendingVerification)
                {
                    // PendingVerification no bloquea si la cuenta puede operar
                    // Permitir actualizar servicios durante verificación
                }
                else if (expertProfile.StripeStatus != StripeStatus.Approved || !expertProfile.OnboardingCompleted)
                {
                    var statusMessage = GetStripeStatusMessage(expertProfile.StripeStatus);
                    return BadRequest(new { 
                        message = statusMessage,
                        stripeStatus = expertProfile.StripeStatus.ToString(),
                        requiresStripeSetup = expertProfile.StripeStatus == StripeStatus.NotRequested,
                        canRetry = expertProfile.StripeStatus == StripeStatus.Rejected || expertProfile.StripeStatus == StripeStatus.NotRequested
                    });
                }

                if (request.ServiceId <= 0)
                {
                    return BadRequest(new { message = "El ID del servicio es requerido" });
                }

                if (request.ServiceTypeId <= 0)
                {
                    return BadRequest(new { message = "El tipo de servicio es requerido" });
                }

                if (string.IsNullOrWhiteSpace(request.Conditions))
                {
                    return BadRequest(new { message = "El campo Condiciones es requerido" });
                }

                if (request.Price <= 0)
                {
                    return BadRequest(new { message = "El precio debe ser mayor que 0" });
                }

                if (request.DurationInHours <= 0)
                {
                    return BadRequest(new { message = "La duración debe ser mayor que 0" });
                }

                // ✅ VALIDACIÓN: Verificar que al menos un tipo de entregable esté seleccionado
                if (string.IsNullOrWhiteSpace(request.SelectedDeliverableTypes))
                {
                    return BadRequest(new { message = "Debe seleccionar al menos un tipo de archivo de entrega (PDF, Video, etc.)" });
                }

                try
                {
                    var deliverableTypeIds = System.Text.Json.JsonSerializer.Deserialize<int[]>(request.SelectedDeliverableTypes);
                    if (deliverableTypeIds == null || deliverableTypeIds.Length == 0)
                    {
                        return BadRequest(new { message = "Debe seleccionar al menos un tipo de archivo de entrega (PDF, Video, etc.)" });
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    return BadRequest(new { message = "El formato de tipos de entregables no es válido" });
                }

                var (success, newService, imageUrls) = await _searchServiceService.UpdateSearchService(userId, request);
                if (!success)
                {
                    return BadRequest(new { message = "Failed to update service. The service may not exist, may not belong to you, or may be inactive." });
                }

                // Cargar los SelectedDeliverableTypes para la respuesta
                var selectedDeliverableTypes = await _context.SearchServiceDeliverableTypes
                    .Where(ssdt => ssdt.SearchServiceId == newService.Id)
                    .Include(ssdt => ssdt.DeliverableType)
                    .Select(ssdt => new
                    {
                        ssdt.Id,
                        ssdt.DeliverableTypeId,
                        ssdt.IsSelected,
                        DeliverableType = new
                        {
                            ssdt.DeliverableType.Id,
                            ssdt.DeliverableType.Name,
                            ssdt.DeliverableType.DisplayName,
                            ssdt.DeliverableType.Description
                        }
                    })
                    .ToListAsync();

                return Ok(new
                {
                    message = "Search service updated successfully",
                    searchService = new
                    {
                        newService.Id,
                        newService.ExpertProfileId,
                        newService.CategoryId,
                        newService.ServiceTypeId,
                        ServiceTypeName = newService.ServiceType?.Name,
                        newService.Price,
                        newService.Conditions,
                        newService.DurationInHours,
                        newService.CreatedAt,
                        newService.IsActive,
                        ImageUrls = imageUrls,
                        SelectedDeliverableTypes = selectedDeliverableTypes
                    },
                    originalServiceId = request.ServiceId
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to update search service", detail = ex.Message });
            }
        }

        [Authorize(Roles = "Expert")] // 🔐 SEGURIDAD: Solo expertos pueden eliminar servicios
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteSearchService(int id)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }
                var success = await _searchServiceService.DeleteSearchService(id, userId);
                
                if (!success)
                {
                    return NotFound(new { message = "Service not found or you don't have permission to delete it" });
                }

                return Ok(new { message = "Search service deleted successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to delete search service", detail = ex.Message });
            }
        }

        /// <summary>
        /// Obtiene el muro de servicios para la homepage con servicios cercanos y populares
        /// ✅ El país se detecta automáticamente desde la IP del cliente
        /// ✅ Usa la capital del país detectado para buscar servicios cercanos
        /// </summary>
        /// <param name="categoryId">ID de la categoría (REQUERIDO) - Filtra servicios por categoría específica</param>
        /// <param name="latitude">⚠️ DEPRECADO: Ya no se usa - el país se detecta desde la IP del cliente</param>
        /// <param name="longitude">⚠️ DEPRECADO: Ya no se usa - el país se detecta desde la IP del cliente</param>
        /// <param name="countryCode">⚠️ DEPRECADO: Ya no se usa - el país se detecta desde la IP del cliente</param>
        /// <param name="locationRange">Rango de búsqueda en km (por defecto 50)</param>
        /// <param name="nearbyPage">Página para servicios cercanos (por defecto 1)</param>
        /// <param name="nearbyPageSize">Tamaño de página para servicios cercanos (por defecto 20, máximo 50)</param>
        /// <param name="popularPage">Página para servicios populares (por defecto 1)</param>
        /// <param name="popularPageSize">Tamaño de página para servicios populares (por defecto 20, máximo 50)</param>
        /// <returns>Array plano con secciones: "Revisiones en [País]" (detectado por IP), "Revisiones populares", y secciones específicas por país (solo para Coches y Motos)</returns>
        /// <remarks>
        /// ✅ El país se detecta automáticamente desde la IP del cliente usando ip-api.com
        /// ✅ No se requieren parámetros latitude/longitude/countryCode - se usa la capital del país detectado
        /// ✅ El título de la primera sección será "Revisiones en [País]" (ej: "Revisiones en España")
        /// ✅ CACHÉ: Solo se cachea para países específicos (DE, FR, PT, MA, IT) y categorías específicas (Coches, Motos, Inmobiliaria)
        /// ✅ CACHÉ: TTL de 5 minutos para respuestas cacheadas
        /// </remarks>
        [HttpGet("homepage-wall")]
        [AllowAnonymous] // ✅ PÚBLICO: Permitir acceso sin autenticación para homepage
        // ⚠️ NOTA: ResponseCache deshabilitado porque ahora detectamos país por IP del cliente
        // Cada cliente puede tener un país diferente, así que el caché HTTP no es apropiado aquí
        // Si necesitas caché, considera implementar un caché en memoria que varíe por (categoryId, countryCode, page)
        public async Task<IActionResult> GetHomepageWall(
            [FromQuery] int categoryId,  // ✅ REQUERIDO: Filtro por categoría (obligatorio)
            [FromQuery] string? latitude = null,
            [FromQuery] string? longitude = null,
            [FromQuery] string? countryCode = null,
            [FromQuery] int locationRange = 50,
            [FromQuery] int nearbyPage = 1,
            [FromQuery] int nearbyPageSize = 20,
            [FromQuery] int popularPage = 1,
            [FromQuery] int popularPageSize = 20)
        {
            var startTime = DateTime.UtcNow;
            var requestId = Guid.NewGuid().ToString("N")[..8];
            
            _logger.LogInformation($"[HOMEPAGE-ENDPOINT] ========================================");
            _logger.LogInformation($"[HOMEPAGE-ENDPOINT] 🏠 HOMEPAGE ENDPOINT: /api/SearchService/homepage-wall");
            _logger.LogInformation($"[HOMEPAGE-ENDPOINT] 📥 GetHomepageWall INICIADO - RequestId: {requestId}");
            _logger.LogInformation($"[HOMEPAGE-ENDPOINT]    Timestamp: {startTime:yyyy-MM-dd HH:mm:ss.fff}");
            _logger.LogInformation($"[HOMEPAGE-ENDPOINT]    categoryId: {categoryId}");
            
            // ✅ DESARROLLO: Timeout aumentado a 120 segundos para desarrollo (PC lento)
            // La connection string tiene CommandTimeout=120, así que 120s es razonable para desarrollo
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                // Validar que categoryId sea válido
                if (categoryId <= 0)
                {
                    _logger.LogWarning($"[ENDPOINT] ❌ GetHomepageWall - categoryId inválido: {categoryId}");
                    return BadRequest(new { message = "categoryId es requerido y debe ser mayor a 0" });
                }
                
                // Validar parámetros de paginación
                if (nearbyPage < 1) nearbyPage = 1;
                if (nearbyPageSize < 1 || nearbyPageSize > 50) nearbyPageSize = 20;
                if (popularPage < 1) popularPage = 1;
                if (popularPageSize < 1 || popularPageSize > 50) popularPageSize = 20;
                if (locationRange <= 0) locationRange = 50;

                // ✅ Obtener nombre de categoría PRIMERO (necesario para verificar si debe cachear)
                _logger.LogInformation($"[ENDPOINT] 🔍 GetHomepageWall - Obteniendo nombre de categoría para categoryId: {categoryId}");
                var category = await _context.Categories
                    .AsNoTracking()
                    .Where(c => c.Id == categoryId && c.IsActive)
                    .Select(c => c.Name)
                    .FirstOrDefaultAsync(cts.Token);
                
                if (category == null)
                {
                    _logger.LogWarning($"[ENDPOINT] ❌ GetHomepageWall - Categoría con ID {categoryId} no encontrada o no está activa");
                    return NotFound(new { message = $"Categoría con ID {categoryId} no encontrada o no está activa" });
                }
                
                string categoryName = category;
                _logger.LogInformation($"[ENDPOINT] ✅ GetHomepageWall - Categoría obtenida: {categoryName}");

                // ✅ NUEVO: Obtener IP del cliente y detectar país desde IP
                var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                _logger.LogInformation($"[ENDPOINT] 🌍 GetHomepageWall - IP del cliente: {clientIp}");
                
                // Obtener país desde IP
                var detectedCountryCode = await _timezoneService.GetCountryFromIpAddressAsync(clientIp);
                _logger.LogInformation($"[ENDPOINT] ✅ GetHomepageWall - País detectado desde IP: {detectedCountryCode}");
                
                // Obtener nombre del país para el título
                var countryDisplayName = GetCountryDisplayName(detectedCountryCode);
                _logger.LogInformation($"[ENDPOINT] ✅ GetHomepageWall - Nombre del país: {countryDisplayName}");

                // ✅ CACHÉ: Verificar si debe cachear (solo para países y categorías específicos)
                bool shouldCache = CachedCountries.Contains(detectedCountryCode) && CachedCategories.Contains(categoryName);
                _logger.LogInformation($"[ENDPOINT] 💾 GetHomepageWall - ¿Debe cachear? {shouldCache} (País: {detectedCountryCode}, Categoría: {categoryName})");
                
                // ✅ CACHÉ: Generar clave de caché única
                var cacheKey = $"homepage-wall-{detectedCountryCode}-{categoryId}-{nearbyPage}-{nearbyPageSize}-{popularPage}-{popularPageSize}";
                
                // ✅ CACHÉ: Intentar obtener del caché si está habilitado
                if (shouldCache && _memoryCache.TryGetValue(cacheKey, out List<object>? cachedResult))
                {
                    _logger.LogInformation($"[ENDPOINT] ✅ GetHomepageWall - Resultado obtenido del CACHÉ - RequestId: {requestId}");
                    _logger.LogInformation($"[ENDPOINT]    Total secciones: {cachedResult.Count}");
                    _logger.LogInformation($"[ENDPOINT]    Duración: {(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms (desde caché)");
                    return Ok(cachedResult);
                }
                
                // ✅ Usar la capital del país detectado (no latitude/longitude del cliente)
                // No pasamos latitude/longitude, así que GetNearbyServices usará la capital del país
                _logger.LogInformation($"[ENDPOINT] 🔍 GetHomepageWall - Obteniendo servicios cercanos usando capital de {countryDisplayName}...");
                var nearbyStartTime = DateTime.UtcNow;
                var (nearbyServices, nearbyTotalCount) = await _searchServiceService.GetNearbyServices(
                    null,  // ✅ No usar latitude del cliente
                    null,  // ✅ No usar longitude del cliente
                    detectedCountryCode,  // ✅ Usar país detectado desde IP
                    locationRange,
                    categoryId,  // ✅ Filtrar por categoría si se proporciona
                    nearbyPage,
                    nearbyPageSize,
                    cts.Token);
                var nearbyDuration = (DateTime.UtcNow - nearbyStartTime).TotalMilliseconds;
                var nearbyServicesList = nearbyServices.ToList();
                _logger.LogInformation($"[ENDPOINT] ✅ GetHomepageWall - Servicios cercanos obtenidos: {nearbyServicesList.Count} servicios, Total: {nearbyTotalCount}, Duración: {nearbyDuration:F2}ms");

                // Obtener servicios populares
                _logger.LogInformation($"[ENDPOINT] 🔍 GetHomepageWall - Obteniendo servicios populares...");
                var popularStartTime = DateTime.UtcNow;
                var (popularServices, popularTotalCount) = await _searchServiceService.GetPopularServices(
                    categoryId,  // ✅ Filtrar por categoría si se proporciona
                    popularPage,
                    popularPageSize,
                    cts.Token);
                var popularDuration = (DateTime.UtcNow - popularStartTime).TotalMilliseconds;
                var popularServicesList = popularServices.ToList();
                _logger.LogInformation($"[ENDPOINT] ✅ GetHomepageWall - Servicios populares obtenidos: {popularServicesList.Count} servicios, Total: {popularTotalCount}, Duración: {popularDuration:F2}ms");
                
                // ✅ Obtener userId del usuario autenticado (si existe)
                int? userId = null;
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!string.IsNullOrEmpty(userIdClaim) && int.TryParse(userIdClaim, out int parsedUserId))
                {
                    userId = parsedUserId;
                }
                
                // ✅ Recolectar todos los IDs de servicios para verificar favoritos
                var allServiceIds = new List<int>();
                allServiceIds.AddRange(nearbyServicesList.Select(s => s.Id));
                allServiceIds.AddRange(popularServicesList.Select(s => s.Id));
                
                // ✅ Verificar favoritos de forma eficiente (una sola consulta a la BD)
                Dictionary<int, bool> favoritesMap = new Dictionary<int, bool>();
                if (userId.HasValue && allServiceIds.Any())
                {
                    _logger.LogInformation($"[ENDPOINT] 🔍 GetHomepageWall - Verificando favoritos para {allServiceIds.Count} servicios...");
                    var favoritesStartTime = DateTime.UtcNow;
                    
                    // ✅ Consulta directa a la BD para obtener todos los favoritos de una vez
                    var favoriteServiceIds = await _context.SearchServiceFavorites
                        .AsNoTracking()
                        .Where(f => f.UserId == userId.Value && allServiceIds.Contains(f.SearchServiceId))
                        .Select(f => f.SearchServiceId)
                        .ToListAsync(cts.Token);
                    
                    // Crear mapa de favoritos
                    foreach (var serviceId in allServiceIds)
                    {
                        favoritesMap[serviceId] = favoriteServiceIds.Contains(serviceId);
                    }
                    
                    var favoritesDuration = (DateTime.UtcNow - favoritesStartTime).TotalMilliseconds;
                    _logger.LogInformation($"[ENDPOINT] ✅ GetHomepageWall - Favoritos verificados: {favoriteServiceIds.Count} favoritos encontrados, Duración: {favoritesDuration:F2}ms");
                }
                
                // ✅ Aplicar IsFavorite a cada servicio
                foreach (var service in nearbyServicesList)
                {
                    service.IsFavorite = favoritesMap.TryGetValue(service.Id, out var isFavorite) ? isFavorite : false;
                }
                
                foreach (var service in popularServicesList)
                {
                    service.IsFavorite = favoritesMap.TryGetValue(service.Id, out var isFavorite) ? isFavorite : false;
                }
                
                // ✅ Construir array plano con todas las secciones
                var allSections = new List<object>();
                
                // 1. Sección "Revisiones en [País]" (basado en IP del cliente)
                allSections.Add(new
                {
                    title = $"Revisiones en {countryDisplayName}",  // ✅ Cambiado: "Revisiones en España" en lugar de "Revisiones cerca de mí"
                    services = nearbyServices,
                    pagination = new
                    {
                        page = nearbyPage,
                        pageSize = nearbyPageSize,
                        totalCount = nearbyTotalCount,
                        totalPages = (int)Math.Ceiling(nearbyTotalCount / (double)nearbyPageSize),
                        hasNextPage = nearbyPage * nearbyPageSize < nearbyTotalCount,
                        hasPreviousPage = nearbyPage > 1
                    }
                });
                
                // 2. Sección "Populares"
                allSections.Add(new
                {
                    title = "Revisiones populares",
                    services = popularServices,
                    pagination = new
                    {
                        page = popularPage,
                        pageSize = popularPageSize,
                        totalCount = popularTotalCount,
                        totalPages = (int)Math.Ceiling(popularTotalCount / (double)popularPageSize),
                        hasNextPage = popularPage * popularPageSize < popularTotalCount,
                        hasPreviousPage = popularPage > 1
                    }
                });
                
                // 3. Secciones específicas SOLO para Coches y Motos en DE y GB
                // ✅ Solo mostrar secciones específicas si la categoría es Coches o Motos
                bool shouldShowSpecificSections = false;
                string[]? targetCategories = null;
                
                if (categoryName == "Coches" || categoryName == "Motos")
                {
                    shouldShowSpecificSections = true;
                    // ✅ Solo la categoría específica solicitada (Coches O Motos)
                    targetCategories = new[] { categoryName };
                }
                
                // Solo obtener secciones si es necesario (Coches o Motos)
                if (shouldShowSpecificSections)
                {
                    // Solo países DE (Alemania) y GB (Reino Unido)
                    var targetCountries = new[] { "DE", "GB" };
                    
                    var categoryCountrySections = await _searchServiceService.GetServicesByCategoryAndCountry(
                        maxSections: 10,
                        servicesPerSection: 20,
                        targetCategories: targetCategories,
                        targetCountries: targetCountries, // ✅ Solo DE y GB
                        cts.Token);

                    // Agregar secciones específicas al array
                    foreach (var section in categoryCountrySections)
                    {
                        var (services, totalCount, sectionCategoryName, country) = section.Value;
                        var servicesList = services.ToList();
                        
                        // ✅ Añadir IDs de servicios de estas secciones para verificar favoritos
                        allServiceIds.AddRange(servicesList.Select(s => s.Id));
                        
                        // ✅ Verificar favoritos de servicios de estas secciones si aún no están en el mapa
                        if (userId.HasValue)
                        {
                            var newServiceIds = servicesList.Select(s => s.Id).Where(id => !favoritesMap.ContainsKey(id)).ToList();
                            if (newServiceIds.Any())
                            {
                                // ✅ Consulta directa a la BD para obtener favoritos de estos servicios
                                var newFavoriteServiceIds = await _context.SearchServiceFavorites
                                    .AsNoTracking()
                                    .Where(f => f.UserId == userId.Value && newServiceIds.Contains(f.SearchServiceId))
                                    .Select(f => f.SearchServiceId)
                                    .ToListAsync(cts.Token);
                                
                                foreach (var serviceId in newServiceIds)
                                {
                                    favoritesMap[serviceId] = newFavoriteServiceIds.Contains(serviceId);
                                }
                            }
                        }
                        
                        // ✅ Aplicar IsFavorite a cada servicio de estas secciones
                        foreach (var service in servicesList)
                        {
                            service.IsFavorite = favoritesMap.TryGetValue(service.Id, out var isFavorite) ? isFavorite : false;
                        }
                        
                        // ✅ Asegurar que CategoryName esté presente (ya debería estar, pero por si acaso)
                        foreach (var service in servicesList)
                        {
                            if (string.IsNullOrEmpty(service.CategoryName))
                            {
                                // Si por alguna razón no tiene CategoryName, obtenerlo de la sección
                                service.CategoryName = sectionCategoryName;
                            }
                        }
                        
                        allSections.Add(new
                        {
                            title = $"Revisiones en {GetCountryDisplayName(country)}", // Ej: "Revisiones en Alemania"
                            services = servicesList,
                            categoryName = sectionCategoryName,
                            country = country,
                            pagination = new
                            {
                                page = 1,
                                pageSize = servicesList.Count,
                                totalCount = totalCount,
                                totalPages = (int)Math.Ceiling(totalCount / 20.0),
                                hasNextPage = totalCount > servicesList.Count,
                                hasPreviousPage = false
                            }
                        });
                    }
                }

                // ✅ Devolver array plano sin objetos anidados
                var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogInformation($"[ENDPOINT] ✅ GetHomepageWall COMPLETADO - RequestId: {requestId}");
                _logger.LogInformation($"[ENDPOINT]    Total secciones: {allSections.Count}");
                _logger.LogInformation($"[ENDPOINT]    Duración total: {totalDuration:F2}ms");
                
                // ✅ LOG JSON: Serializar y loguear el JSON que se está devolviendo
                try
                {
                    var jsonResponse = System.Text.Json.JsonSerializer.Serialize(allSections, new System.Text.Json.JsonSerializerOptions 
                    { 
                        WriteIndented = false,
                        MaxDepth = 3 // Limitar profundidad para evitar logs muy largos
                    });
                    var jsonLength = jsonResponse.Length;
                    var jsonPreview = jsonLength > 1000 ? jsonResponse.Substring(0, 1000) + "..." : jsonResponse;
                    _logger.LogInformation($"[ENDPOINT] 📤 JSON RESPONSE - RequestId: {requestId}");
                    _logger.LogInformation($"[ENDPOINT]    JSON Length: {jsonLength} caracteres");
                    _logger.LogInformation($"[ENDPOINT]    JSON Preview (primeros 1000 chars): {jsonPreview}");
                }
                catch (Exception jsonEx)
                {
                    _logger.LogWarning($"[ENDPOINT] ⚠️ Error serializando JSON para log: {jsonEx.Message}");
                }
                
                _logger.LogInformation($"[ENDPOINT] ========================================");
                
                // ✅ CACHÉ: Guardar en caché si está habilitado para este país y categoría
                if (shouldCache)
                {
                    try
                    {
                        var cacheOptions = new MemoryCacheEntryOptions
                        {
                            AbsoluteExpirationRelativeToNow = CacheExpiration,
                            Priority = CacheItemPriority.Normal,
                            SlidingExpiration = null // No usar sliding expiration, solo absolute
                        };
                        
                        _memoryCache.Set(cacheKey, allSections, cacheOptions);
                        _logger.LogInformation($"[ENDPOINT] 💾 GetHomepageWall - Resultado guardado en CACHÉ - Clave: {cacheKey}, TTL: {CacheExpiration.TotalMinutes} minutos");
                    }
                    catch (Exception cacheEx)
                    {
                        // No fallar si hay error al guardar en caché, solo loguear
                        _logger.LogWarning(cacheEx, $"[ENDPOINT] ⚠️ Error al guardar en caché: {cacheEx.Message}");
                    }
                }
                
                return Ok(allSections);
            }
            catch (OperationCanceledException)
            {
                var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogError($"[ENDPOINT] ❌ GetHomepageWall TIMEOUT - RequestId: {requestId}");
                _logger.LogError($"[ENDPOINT]    Duración antes del timeout: {totalDuration:F2}ms");
                _logger.LogError($"[ENDPOINT]    categoryId: {categoryId}, countryCode: {countryCode}");
                
                await _loggingService.LogErrorAsync(
                    message: "Timeout al obtener el muro de homepage",
                    details: "La operación excedió el tiempo máximo de espera (120 segundos)",
                    source: "SearchServiceController.GetHomepageWall",
                    relatedEntityType: "HomepageWall",
                    additionalData: new { latitude, longitude, countryCode, requestId, duration = totalDuration },
                    notifyUser: false
                );
                // ✅ CRÍTICO: Asegurar headers CORS y Content-Type ANTES de devolver StatusCode
                Response.Headers["Access-Control-Allow-Origin"] = Request.Headers["Origin"].ToString();
                Response.Headers["Access-Control-Allow-Credentials"] = "true";
                Response.ContentType = "application/json";
                
                return StatusCode(408, new { message = "Request timeout. Please try again.", detail = "The request took too long to complete" });
            }
            catch (Exception ex)
            {
                var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogError(ex, $"[HOMEPAGE-ENDPOINT] ❌ GetHomepageWall EXCEPCIÓN - RequestId: {requestId}");
                _logger.LogError($"[HOMEPAGE-ENDPOINT]    🏠 HOMEPAGE ENDPOINT: /api/SearchService/homepage-wall");
                _logger.LogError($"[HOMEPAGE-ENDPOINT]    Exception Type: {ex.GetType().Name}");
                _logger.LogError($"[HOMEPAGE-ENDPOINT]    Exception Message: {ex.Message}");
                _logger.LogError($"[HOMEPAGE-ENDPOINT]    Inner Exception: {ex.InnerException?.Message ?? "None"}");
                _logger.LogError($"[HOMEPAGE-ENDPOINT]    StackTrace: {ex.StackTrace}");
                _logger.LogError($"[HOMEPAGE-ENDPOINT]    Duración antes del error: {totalDuration:F2}ms");
                _logger.LogError($"[HOMEPAGE-ENDPOINT]    categoryId: {categoryId}, countryCode: {countryCode ?? "null"}");
                _logger.LogError($"[HOMEPAGE-ENDPOINT] ========================================");
                
                // Log del error con información detallada
                var requestParams = new
                {
                    latitude,
                    longitude,
                    countryCode,
                    locationRange,
                    nearbyPage,
                    nearbyPageSize,
                    popularPage,
                    popularPageSize
                };

                await _loggingService.LogErrorAsync(
                    message: "Error al obtener el muro de homepage",
                    details: $"Exception: {ex.Message}\nStackTrace: {ex.StackTrace}",
                    source: "SearchServiceController.GetHomepageWall",
                    relatedEntityType: "HomepageWall",
                    additionalData: new
                    {
                        requestParameters = requestParams,
                        innerException = ex.InnerException?.Message,
                        exceptionType = ex.GetType().Name,
                        requestId,
                        duration = totalDuration
                    },
                    notifyUser: false
                );

                return StatusCode(500, new { message = "Failed to retrieve homepage wall", detail = ex.Message });
            }
        }
    }
}