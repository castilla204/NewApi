using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using System.Security.Claims;
using newApi.Services;
using newApi.ScrapperGateway.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models;
using newApi.Common;
using Stripe.Checkout;
using Stripe;
using System.Text.Json;
using System.ComponentModel.Design;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SearchController : ControllerBase
    {
        /// <summary>
        /// 🛡️ I6 FIX: helper de truncado UNIFORME para metadata + idempotency key.
        /// Antes había truncado inline (con substring) en metadata pero no en el hash de
        /// idempotency. Usar este helper garantiza que ambos usen exactamente el mismo
        /// algoritmo y resultado. Devuelve "" si input null/empty para evitar diferencias
        /// entre null/empty en el hash.
        /// </summary>
        private static string I6_Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length > maxLength ? value.Substring(0, maxLength) : value;
        }

        private readonly AppDbContext _context;
        private readonly IAuthorizationServices _authService;
        private readonly IUserService _userService;
        private readonly ISubscriptionService _subscriptionService;
        private readonly IStripeValidationService _stripeValidationService;
        private readonly ILoggingService _loggingService;
        private readonly ISignedUrlService _signedUrlService;
        private readonly IConfiguration _configuration;
        private readonly IAvailabilityService _availabilityService;

        // ✅ COMENTADO: Ya no necesario - Stripe usa default automático configurado en Dashboard
        // Según docs oficiales Stripe 2026, se recomienda usar "unspecified" y configurar
        // "Automatic" como default en Dashboard (Tax Settings → "Incluir impuestos en los precios")
        // private static string GetTaxBehaviorForCurrency(string currency)
        // {
        //     return currency?.ToLower() switch
        //     {
        //         "usd" => "exclusive",
        //         "cad" => "exclusive",
        //         _ => "inclusive" // EUR, GBP, MXN, etc.
        //     };
        // }

        public SearchController(
            AppDbContext context,
            IAuthorizationServices authService,
            IUserService userService,
            ISubscriptionService subscriptionService,
            IStripeValidationService stripeValidationService,
            ILoggingService loggingService,
            ISignedUrlService signedUrlService,
            IAvailabilityService availabilityService,
            IConfiguration configuration)
        {
            _context = context;
            _authService = authService;
            _userService = userService;
            _subscriptionService = subscriptionService;
            _stripeValidationService = stripeValidationService;
            _loggingService = loggingService;
            _signedUrlService = signedUrlService;
            _availabilityService = availabilityService;
            _configuration = configuration;
        }

        /// <summary>
        /// Helper method to get StatusId from StatusValue
        /// </summary>
        private async Task<int> GetStatusIdByValueAsync(string statusValue)
        {
            var systemStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.StatusType == "SearchHireStatus");
            
            if (systemStatus == null)
            {
                // ⚠️ FRENTE 8: estado no encontrado → AVISAR en vez de rebobinar a "pending" en silencio.
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: SearchHireStatus value not found - defaulting to 'pending'",
                    details: $"GetStatusIdByValueAsync could not resolve StatusValue '{statusValue}' (SearchHireStatus). Defaulting to pending (1); verify the status is seeded. This can silently misroute a hire.",
                    source: "SearchController.GetStatusIdByValueAsync",
                    relatedEntityType: "SearchHire");
                return 1;
            }

            return systemStatus.Id;
        }

        [HttpGet("debug-auth")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DebugAuth()
        {
            var claims = User.Claims.Select(c => new { c.Type, c.Value }).ToList();
            var roleClaim = User.FindFirst(ClaimTypes.Role)?.Value;
            var isAdmin = _authService.IsAdmin(User);
            
            // Obtener información del usuario de la base de datos
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            object userFromDb = new { };
            
            if (!string.IsNullOrEmpty(userIdClaim) && int.TryParse(userIdClaim, out int userId))
            {
                var user = await _context.Users.FindAsync(userId);
                if (user != null)
                {
                    // Simular la lógica del GenerateJwtToken
                    var roleName = user.Role switch
                    {
                        DataLayer.Models.PostGresModels.UserRole.Client => "Client",
                        DataLayer.Models.PostGresModels.UserRole.Expert => "Expert", 
                        DataLayer.Models.PostGresModels.UserRole.Admin => "Admin",
                        _ => "Client"
                    };
                    
                    userFromDb = new
                    {
                        userId = user.Id,
                        email = user.Email,
                        roleNumeric = (int)user.Role,
                        roleEnum = user.Role.ToString(),
                        roleName = roleName
                    };
                }
            }
            
            return Ok(new
            {
                claims = claims,
                roleClaim = roleClaim,
                isAdmin = isAdmin,
                userIdentity = User.Identity?.Name,
                isAuthenticated = User.Identity?.IsAuthenticated,
                userFromDatabase = userFromDb
            });
        }


        [HttpGet("all")]
        public async Task<IActionResult> GetAllSearches([FromQuery] SearchListRequestDto request)
        {
            try
            {
                if (!_authService.IsAdmin(User))
                {
                    return Unauthorized(new { message = "Admin access required" });
                }

                // Validar parámetros
                if (request.Page < 1) request.Page = 1;
                if (request.PageSize < 1 || request.PageSize > 50) request.PageSize = 20;

                // Construir query base con includes
                var query = _context.Searches
                    .Include(s => s.User)
                    .Include(s => s.SearchParameters)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                        .ThenInclude(e => e.ExpertProfile)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.SearchService)
                        .ThenInclude(ss => ss.Images)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                        .ThenInclude(st => st.ServiceTypeCategory)
                    .AsQueryable();

                // Aplicar filtros
                if (!string.IsNullOrEmpty(request.SearchTerm))
                {
                    var searchTerm = request.SearchTerm.ToLower();
                    query = query.Where(s => 
                        s.Title.ToLower().Contains(searchTerm) || 
                        s.Description.ToLower().Contains(searchTerm));
                }

                if (request.Category.HasValue)
                {
                    query = query.Where(s => s.SearchParameters.Any(sp => sp.Category == request.Category.Value));
                }

                if (request.IsActive.HasValue)
                {
                    query = query.Where(s => s.IsActive == request.IsActive.Value);
                }

                if (request.IsRevised.HasValue)
                {
                    query = query.Where(s => s.IsRevised == request.IsRevised.Value);
                }

                if (!string.IsNullOrEmpty(request.SearchHireStatus))
                {
                    var statusId = await GetStatusIdByValueAsync(request.SearchHireStatus);
                    query = query.Where(s => s.SearchHire != null && s.SearchHire.StatusId == statusId);
                }

                // Contar total de resultados
                var totalCount = await query.CountAsync();

                // Aplicar ordenamiento
                query = ApplySorting(query, request.SortBy, request.SortDirection);

                // Aplicar paginación
                var searches = await query
                    .Skip((request.Page - 1) * request.PageSize)
                    .Take(request.PageSize)
                    .ToListAsync();

                // ✅ Obtener IDs de expertos para cargar disponibilidades
                var expertIds = searches
                    .Where(s => s.SearchHire?.Expert?.ExpertProfile != null)
                    .Select(s => s.SearchHire.Expert.ExpertProfile.Id)
                    .Distinct()
                    .ToList();

                // ✅ Cargar disponibilidades de expertos
                var availabilities = new Dictionary<int, ExpertAvailability>();
                if (expertIds.Any())
                {
                    var expertAvailabilities = await _context.ExpertAvailabilities
                        .Where(ea => expertIds.Contains(ea.ExpertId) && ea.IsActive && ea.EffectiveTo == null)
                        .OrderByDescending(ea => ea.EffectiveFrom)
                        .GroupBy(ea => ea.ExpertId)
                        .Select(g => g.First())
                        .ToListAsync();

                    foreach (var availability in expertAvailabilities)
                    {
                        availabilities[availability.ExpertId] = availability;
                    }
                }

                // ✅ Obtener IDs de categorías únicos de los SearchParameters
                var categoryIds = searches
                    .SelectMany(s => s.SearchParameters)
                    .Where(sp => sp.Category.HasValue)
                    .Select(sp => sp.Category!.Value)
                    .Distinct()
                    .ToList();

                // ✅ Cargar nombres de categorías
                var categoryNames = new Dictionary<int, string>();
                if (categoryIds.Any())
                {
                    var categories = await _context.Categories
                        .AsNoTracking()
                        .Where(c => categoryIds.Contains(c.Id) && c.IsActive)
                        .Select(c => new { c.Id, c.Name })
                        .ToListAsync();

                    foreach (var category in categories)
                    {
                        categoryNames[category.Id] = category.Name;
                    }
                }

                // Mapear a DTOs
                var searchDtos = searches.Select(s =>
                {
                    // ✅ Obtener primera imagen del servicio (usando misma lógica que ResolveServiceImageUrl)
                    string? serviceImageUrl = null;
                    if (s.SearchHire?.SearchService?.Images != null && s.SearchHire.SearchService.Images.Any())
                    {
                        var firstImage = s.SearchHire.SearchService.Images.OrderBy(img => img.Id).First();
                        
                        // ✅ Si la URL es externa (no de Google Cloud Storage), devolverla directamente
                        if (!string.IsNullOrWhiteSpace(firstImage.ImageUrl))
                        {
                            var bucketName = _configuration["GoogleCloud:BucketName"];
                            var isExternalUrl = string.IsNullOrWhiteSpace(bucketName) || 
                                               !firstImage.ImageUrl.Contains($"storage.googleapis.com/{bucketName}", StringComparison.OrdinalIgnoreCase);
                            
                            if (isExternalUrl)
                            {
                                // URL externa (Unsplash, Pexels, etc.) - devolver directamente sin signed URL
                                serviceImageUrl = firstImage.ImageUrl;
                            }
                            else
                            {
                                // ✅ Si es URL de Google Cloud Storage o hay ImageObjectName, generar signed URL
                                serviceImageUrl = _signedUrlService.GetSignedUrl(firstImage.ImageObjectName ?? string.Empty) ?? firstImage.ImageUrl;
                            }
                        }
                        else
                        {
                            // Fallback si no hay ImageUrl
                            serviceImageUrl = _signedUrlService.GetSignedUrl(firstImage.ImageObjectName ?? string.Empty) ?? string.Empty;
                        }
                    }

                    // ✅ Obtener disponibilidad del experto
                    HomepageExpertAvailabilityDto? expertAvailability = null;
                    if (s.SearchHire?.Expert?.ExpertProfile != null && 
                        availabilities.TryGetValue(s.SearchHire.Expert.ExpertProfile.Id, out var availability))
                    {
                        var daysOfWeek = System.Text.Json.JsonSerializer.Deserialize<List<string>>(availability.DaysOfWeek) ?? new List<string>();
                        expertAvailability = new HomepageExpertAvailabilityDto
                        {
                            DaysOfWeek = daysOfWeek,
                            StartTime = availability.StartTime,
                            EndTime = availability.EndTime
                        };
                    }

                    // ✅ Obtener ciudad del experto
                    string? expertCity = null;
                    if (s.SearchHire?.Expert?.ExpertProfile != null)
                    {
                        expertCity = s.SearchHire.Expert.ExpertProfile.City;
                    }

                    // ✅ Obtener categoría y nombre de categoría
                    var categoryId = s.SearchParameters.FirstOrDefault()?.Category ?? 0;
                    var categoryName = categoryId > 0 && categoryNames.TryGetValue(categoryId, out var name) 
                        ? name 
                        : null;

                    return new SearchListDto
                {
                    Id = s.Id,
                    UserId = s.UserId,
                    Title = s.Title,
                    Description = s.Description,
                    Frequency = s.Frequency,
                    IsActive = s.IsActive,
                    IsRevised = s.IsRevised,
                    LastExecution = s.LastExecution,
                    CreatedAt = s.CreatedAt,
                    StartDate = s.StartDate,
                        Category = categoryId,
                        CategoryName = categoryName, // ✅ NUEVO: Nombre de la categoría
                    User = new UserDto
                    {
                        Email = s.User.Email,
                        Name = s.User.Name
                    },
                    SearchHire = s.SearchHire != null ? new SearchHireDto
                    {
                        Id = s.SearchHire.Id,
                        ExpertId = s.SearchHire.ExpertId ?? 0,
                        Status = s.SearchHire.Status.StatusValue,
                        StatusTranslated = SearchHireStatusExtensions.ToSpanishTranslation(s.SearchHire.Status.StatusValue),
                        CreatedAt = s.SearchHire.CreatedAt,
                        Amount = s.SearchHire.Amount, // ✅ STRIPE TAX: Monto total con IVA
                        BaseAmount = s.SearchHire.BaseAmount, // ✅ STRIPE TAX: Base sin IVA
                        TaxAmount = s.SearchHire.TaxAmount, // ✅ STRIPE TAX: IVA calculado
                        // Round 24: snapshot currency del cargo real (Round 21).
                        ChargeCurrency = string.IsNullOrEmpty(s.SearchHire.Currency) ? "EUR" : s.SearchHire.Currency,
                        ExpertTimezone = s.SearchHire.ExpertTimezone, // ✅ INTERNACIONALIZACIÓN
                        ExpertCountry = s.SearchHire.ExpertCountry, // ✅ INTERNACIONALIZACIÓN
                        // 🛡️ Round 10 — P-C FIX (V8 snapshots): exponer al frontend
                        ClientPercentageSnapshot = s.SearchHire.ClientPercentageSnapshot,
                        ExpertPercentageSnapshot = s.SearchHire.ExpertPercentageSnapshot,
                        PlatformPercentageSnapshot = s.SearchHire.PlatformPercentageSnapshot,
                          Expert = s.SearchHire.Expert != null ? new UserDto
                          {
                              Name = s.SearchHire.Expert.Name,
                              ProfilePictureUrl = ResolveProfilePictureUrl(s.SearchHire.Expert.ExpertProfile)
                          } : null,
                        // ✅ NUEVO: Información completa del estado con colores
                        StatusInfo = s.SearchHire.Status != null ? new SystemStatusDto
                        {
                            Id = s.SearchHire.Status.Id,
                            StatusType = s.SearchHire.Status.StatusType,
                            StatusName = s.SearchHire.Status.StatusName,
                            StatusValue = s.SearchHire.Status.StatusValue,
                            DisplayName = s.SearchHire.Status.DisplayName,
                            Description = s.SearchHire.Status.Description,
                            Color = s.SearchHire.Status.Color,
                            IsActive = s.SearchHire.Status.IsActive,
                            IsFinalizationStatus = s.SearchHire.Status.IsFinalizationStatus,
                            SortOrder = s.SearchHire.Status.SortOrder,
                            CreatedAt = s.SearchHire.Status.CreatedAt,
                            UpdatedAt = s.SearchHire.Status.UpdatedAt
                            } : null,
                            // ✅ NUEVO: Mapear información del servicio
                            Service = s.SearchHire.SearchService != null ? new ServiceInfo
                            {
                                Id = s.SearchHire.SearchService.Id,
                                ServiceTypeId = s.SearchHire.SearchService.ServiceTypeId,
                                ServiceTypeName = s.SearchHire.SearchService.ServiceType?.Name ?? "Unknown Service Type",
                                ServiceTypeCategoryId = s.SearchHire.SearchService.ServiceType?.ServiceTypeCategoryId,
                                ServiceTypeCategoryName = s.SearchHire.SearchService.ServiceType?.ServiceTypeCategory?.Name,
                                RequiresAppointment = s.SearchHire.SearchService.ServiceType?.RequiresAppointment ?? false,
                                // 🛡️ SNAPSHOT CONTRACTUAL: precio contratado (BaseAmount del hire), no el actual.
                                Price = s.SearchHire.BaseAmount ?? s.SearchHire.SearchService.Price,
                                Conditions = s.SearchHire.ConditionsSnapshot ?? s.SearchHire.SearchService.Conditions,
                                DurationInHours = s.SearchHire.DurationInHoursSnapshot ?? s.SearchHire.SearchService.DurationInHours,
                                // Round 24: poblar Currency (default 'EUR' si null).
                                Currency = string.IsNullOrEmpty(s.SearchHire.SearchService.Currency) ? "EUR" : s.SearchHire.SearchService.Currency,
                                // 🛡️ SNAPSHOT CONTRACTUAL: ubicación/radio del experto AL CONTRATAR
                                // (fallback al dato vivo para hires anteriores a la columna).
                                ExpertLatitude = s.SearchHire.ExpertLatitudeSnapshot ?? s.SearchHire.SearchService.ExpertProfile?.Latitude,
                                ExpertLongitude = s.SearchHire.ExpertLongitudeSnapshot ?? s.SearchHire.SearchService.ExpertProfile?.Longitude,
                                LocationRange = s.SearchParameters?.FirstOrDefault()?.LocationRange ?? 50,
                                ExpertWorkRadiusKm = s.SearchHire.ExpertWorkRadiusKmSnapshot ?? s.SearchHire.SearchService.ExpertProfile?.WorkRadiusKm
                        } : null
                        } : null,
                        // ✅ NUEVO: Imagen del servicio, horario y ciudad del experto
                        ServiceImageUrl = serviceImageUrl,
                        ExpertAvailability = expertAvailability,
                        ExpertCity = expertCity
                    };
                }).ToList();

                // Crear respuesta paginada
                var response = new SearchListResponseDto
                {
                    Searches = searchDtos,
                    Pagination = new PaginationMetadata
                    {
                        CurrentPage = request.Page,
                        PageSize = request.PageSize,
                        TotalCount = totalCount,
                        TotalPages = (int)Math.Ceiling((double)totalCount / request.PageSize),
                        HasPrevious = request.Page > 1,
                        HasNext = request.Page < (int)Math.Ceiling((double)totalCount / request.PageSize)
                    }
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                await _loggingService.LogErrorAsync(
                    message: "Error al obtener todas las búsquedas (admin)",
                    details: $"Error en GetAllSearches. Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    source: "SearchController.GetAllSearches",
                    relatedEntityType: "Search");

                return StatusCode(500, new { message = "Ha ocurrido un error al procesar la solicitud.", errorCode = "GET_ALL_SEARCHES_ERROR" });
            }
        }

        /// <summary>
        /// Aplica ordenamiento a la query según los parámetros especificados
        /// </summary>
        private static IQueryable<Search> ApplySorting(IQueryable<Search> query, string? sortBy, string? sortDirection)
        {
            var isDescending = sortDirection?.ToLower() == "desc";

            return sortBy?.ToLower() switch
            {
                "title" => isDescending ? query.OrderByDescending(s => s.Title) : query.OrderBy(s => s.Title),
                "description" => isDescending ? query.OrderByDescending(s => s.Description) : query.OrderBy(s => s.Description),
                "frequency" => isDescending ? query.OrderByDescending(s => s.Frequency) : query.OrderBy(s => s.Frequency),
                "isactive" => isDescending ? query.OrderByDescending(s => s.IsActive) : query.OrderBy(s => s.IsActive),
                "isrevised" => isDescending ? query.OrderByDescending(s => s.IsRevised) : query.OrderBy(s => s.IsRevised),
                "lastexecution" => isDescending ? query.OrderByDescending(s => s.LastExecution) : query.OrderBy(s => s.LastExecution),
                "startdate" => isDescending ? query.OrderByDescending(s => s.StartDate) : query.OrderBy(s => s.StartDate),
                "userid" => isDescending ? query.OrderByDescending(s => s.UserId) : query.OrderBy(s => s.UserId),
                _ => isDescending ? query.OrderByDescending(s => s.CreatedAt) : query.OrderBy(s => s.CreatedAt)
            };
        }

        [HttpPost("create-with-hire")]
        public async Task<IActionResult> CreateSearchWithHire([FromBody] CreateSearchWithHireDto request)
        {
            try
            {
                var searchDto = request.SearchDto;
                var parameterDto = request.ParameterDto;

                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // 🗓️ F1 FIX: validar el hueco de cita ANTES de crear el Checkout Session. Sin esto, un
                // EndsAtUtc<=StartsAtUtc dispara el CHECK 23514 en el webhook (tras autorizar el pago),
                // escapa del filtro 23P01 → webhook 500 → Stripe reintenta ~3 días y la autorización del
                // cliente queda retenida ~7 días. Validamos aquí y devolvemos 400 sin cobrar nada.
                if (request.StartsAtUtc.HasValue || request.EndsAtUtc.HasValue)
                {
                    if (!request.StartsAtUtc.HasValue || !request.EndsAtUtc.HasValue)
                        return BadRequest(new { message = "Cita inválida: faltan inicio o fin del hueco." });
                    if (request.EndsAtUtc.Value <= request.StartsAtUtc.Value)
                        return BadRequest(new { message = "Cita inválida: el fin debe ser posterior al inicio." });
                    if (request.StartsAtUtc.Value <= DateTime.UtcNow)
                        return BadRequest(new { message = "Cita inválida: el hueco ya ha pasado, elige otro." });

                    // LEAD-FIX: antelación mínima en modo self. Sin esto, un hueco a +minutos colapsa
                    // ExpertConfirmationDeadline = min(now+48h, slotStart) → el experto no llega a confirmar
                    // → auto-cancelación ("cita fantasma"). El modo seller NO pasa por aquí (no envía
                    // StartsAtUtc en el checkout; tiene su propia ventana +3 días). Defensa explícita además
                    // del filtro de SlotCalculator (IsSlotBookableAsync ya no ofrecería el hueco).
                    if (request.StartsAtUtc.Value < DateTime.UtcNow.AddHours(SelfBookingPolicy.LeadTimeHours))
                        return BadRequest(new { message = $"Cita inválida: elige un hueco con al menos {SelfBookingPolicy.LeadTimeHours}h de antelación." });

                    // 🔒 Slot-trust: el cliente NO puede reservar un hueco que el calendario no ofrece.
                    // Re-validamos contra la disponibilidad real (reglas + duración) ANTES del Checkout.
                    var slotOk = await _availabilityService.IsSlotBookableAsync(
                        searchDto.ServiceId, request.StartsAtUtc.Value, request.EndsAtUtc.Value);
                    if (!slotOk)
                        return BadRequest(new { message = "Cita inválida: ese hueco ya no está disponible, elige otro." });
                }

                // 🛡️ FIX [SELF-2]: validar rango de las coordenadas de la cita ANTES del Checkout (mismo
                // patrón que SellerBookingController M3 FIX). El flujo self metía request.Latitude/Longitude
                // en el metadata sin validar [-90,90]/[-180,180]; una coord fuera de rango ensuciaba la cita
                // (pin imposible). Solo se valida cuando hay valor.
                {
                    decimal? apLat = decimal.TryParse(request.Latitude, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var apLatV) ? apLatV : (decimal?)null;
                    decimal? apLng = decimal.TryParse(request.Longitude, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var apLngV) ? apLngV : (decimal?)null;
                    if ((apLat.HasValue && (apLat < -90 || apLat > 90)) ||
                        (apLng.HasValue && (apLng < -180 || apLng > 180)))
                        return BadRequest(new { message = "Cita inválida: latitud debe estar entre -90 y 90, y longitud entre -180 y 180." });
                }

                var activeSearchCount = await _context.Searches.CountAsync(s => s.UserId == userId && s.IsActive);
                var subscriptionLimits = await _subscriptionService.GetUserSubscriptionLimits(userId);

                var user = await _userService.GetUserAsync(userId);
                if (user == null)
                {
                    return NotFound(new { message = "User not found" });
                }

                // ✅ VALIDACIÓN: Usuario bloqueado no puede crear búsquedas ni contratar
                if (user.IsBlocked)
                {
                    await _loggingService.LogWarningAsync(
                        message: "Blocked user attempted to create search with hire",
                        details: $"Blocked user {user.Id} ({user.Email}) attempted to create search with hire",
                        userId: user.Id,
                        source: "SearchController.CreateSearchWithHire",
                        relatedEntityType: "User",
                        relatedEntityId: user.Id
                    );
                    return Unauthorized(new { message = "User account is blocked" });
                }

                // 🚨 VALIDACIÓN CRÍTICA: Los expertos no pueden crear contrataciones como clientes
                // ✅ IMPORTANTE: Deben usar una cuenta distinta (no registrada como experto) para contratar
                // ✅ Esta validación DEBE hacerse ANTES de crear el checkout session
                // ✅ MEJORA: Verificar explícitamente si tiene ExpertProfile en la BD (no solo en memoria)
                var userWithProfile = await _context.Users
                    .Include(u => u.ExpertProfile)
                    .FirstOrDefaultAsync(u => u.Id == userId);
                
                if (userWithProfile == null)
                {
                    return NotFound(new { message = "User not found" });
                }

                var hasExpertProfile = await _context.ExpertProfiles
                    .AnyAsync(ep => ep.UserId == userId);

                if (userWithProfile.Role == DataLayer.Models.PostGresModels.UserRole.Expert || hasExpertProfile || userWithProfile.ExpertProfile != null)
                {
                    await _loggingService.LogWarningAsync(
                        message: "Expert attempted to create contract as client",
                        details: $"User {userId} (Email: {userWithProfile.Email}, Role: {userWithProfile.Role}, HasExpertProfile: {hasExpertProfile}) attempted to create a contract as client via CreateSearchWithHire. Blocked.",
                        userId: userId,
                        source: "SearchController.CreateSearchWithHire",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new { 
                            UserId = userId,
                            UserEmail = userWithProfile.Email,
                            UserRole = userWithProfile.Role.ToString(),
                            HasExpertProfileInMemory = userWithProfile.ExpertProfile != null,
                            HasExpertProfileInDb = hasExpertProfile
                        }
                    );
                    
                    return BadRequest(new { 
                        message = "Los expertos no pueden crear contrataciones. Debes usar una cuenta distinta (no registrada como experto) para contratar servicios."
                    });
                }

                // 📱 La verificación de móvil/SMS NO se exige al CLIENTE para contratar. Solo los
                // EXPERTOS deben verificar su móvil (para ser visibles en el catálogo, gate en
                // SearchServiceService + /api/User/expert-visibility). El cliente puede contratar sin
                // móvil verificado; si tiene un móvil (capturado en checkout/perfil) recibirá los SMS,
                // pero no es obligatorio.

                var service = await _context.SearchServices
                    .Include(ss => ss.ExpertProfile)
                    // ✅ CHECKOUT UX: ServiceType/Category/Images alimentan el line item que ve el comprador
                    // en la página alojada de Stripe (nombre real del servicio, descripción y foto) en lugar
                    // de "Payment for Service {id}".
                    .Include(ss => ss.ServiceType)
                    .Include(ss => ss.Category)
                    .Include(ss => ss.Images)
                    .FirstOrDefaultAsync(ss => ss.Id == searchDto.ServiceId);
                if (service == null)
                {
                    return NotFound(new { message = "Service not found" });
                }

                // 🚨 FIX C9: servicio SIN experto (ExpertProfileId nullable, FK OnDelete SetNull → un servicio
                // cuyo experto fue borrado queda con ExpertProfile == null). Sin experto no hay destino de payout
                // ni contratación válida. Rechazar ANTES de crear la Checkout Session.
                if (service.ExpertProfile == null)
                {
                    return BadRequest(new { message = "Este servicio no está disponible para contratar" });
                }

                // ✅ VALIDACIÓN CENTRALIZADA: Verificar que el experto puede recibir pagos
                if (service.ExpertProfile != null)
                {
                    var validationResult = await _stripeValidationService.ValidateExpertCanReceivePaymentsAsync(
                        service.ExpertProfile, "crear búsqueda");
                    
                    if (!validationResult.IsValid)
                    {
                        return BadRequest(new { 
                            message = validationResult.ErrorMessage,
                            stripeStatus = validationResult.StripeStatus,
                            requiresStripeSetup = validationResult.RequiresStripeSetup,
                            canRetry = validationResult.CanRetry
                        });
                    }
                }

                // 🚨 VALIDACIÓN CRÍTICA: Verificar que el experto no se contrate a sí mismo
                // ✅ IMPORTANTE: Esta validación DEBE hacerse ANTES de crear el checkout session
                // para evitar perder comisiones de Stripe al hacer refunds
                if (service.ExpertProfile != null && service.ExpertProfile.UserId == userId)
                {
                    return BadRequest(new { message = "No puedes contratarte a ti mismo como experto" });
                }

                // 🛡️ FIX [SELF-1]: validar en SERVIDOR que la dirección de la cita (modo self) está dentro
                // del radio de servicio del experto. El frontend ya lo valida, pero un cliente por curl/bypass
                // podía reservar una inspección a CUALQUIER distancia (otra ciudad/país) y forzar al experto a
                // desplazarse fuera de su zona. Bound GENEROSO = máx(WorkRadiusKm del experto, rango de búsqueda
                // del cliente) +25% de margen de geocoding, para NO rechazar nada que el frontend sí permitía;
                // solo corta abusos evidentes. Taller (WorkRadiusKm==0 y sin rango) o coords ausentes/ilegibles
                // se omiten (la confirmación del experto sigue siendo el gate final). Pre-pago → 400 sin cobrar.
                if (request.StartsAtUtc.HasValue
                    && decimal.TryParse(request.Latitude, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var apptLatVal)
                    && decimal.TryParse(request.Longitude, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var apptLngVal)
                    && decimal.TryParse(service.ExpertProfile.Latitude, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var expLatVal)
                    && decimal.TryParse(service.ExpertProfile.Longitude, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var expLngVal))
                {
                    // 🛡️ BUG #11 FIX [SELF-1b]: LocationRange lo envía el CLIENTE (DTO sin [Range] ni clamp), así
                    // que un valor inflado (p.ej. 999999) hacía boundKm enorme y ANULABA el guard de radio. Lo
                    // topamos al máximo legítimo del producto = 200 km (techo del radio de búsqueda del front
                    // getNextSearchRadiusKm→min(200,…) y del WorkRadiusKm del experto MAX_WORK_RADIUS_KM=200).
                    // No rechaza ningún flujo legítimo (incluso con el margen +25% tolera ~250 km).
                    const int MAX_SEARCH_RADIUS_KM = 200;
                    var clampedRange = Math.Min(parameterDto?.LocationRange ?? 0, MAX_SEARCH_RADIUS_KM);
                    decimal boundKm = Math.Max(service.ExpertProfile.WorkRadiusKm, clampedRange);
                    if (boundKm > 0)
                    {
                        var apptDistanceKm = global::newApi.Services.SearchServiceService.CalculateDistance(expLatVal, expLngVal, apptLatVal, apptLngVal);
                        if (apptDistanceKm > boundKm * 1.25m)
                        {
                            return BadRequest(new { message = $"La dirección de la cita está fuera del radio de servicio del experto (~{apptDistanceKm:F0} km). Elige una dirección dentro de su zona de cobertura." });
                        }
                    }
                }

                // 🚨 A2: PROTECCIÓN CONTRA CONTRATACIONES DUPLICADAS (anti doble-submit).
                // Antes este endpoint (el principal de contratación) NO comprobaba si el cliente ya
                // tenía una contratación activa para el servicio, a diferencia de HireService/LoadMoneyService.
                // Sin esto, un doble clic / reintento creaba DOS checkout sessions -> DOS PaymentIntents,
                // pudiendo cobrar dos veces al cliente. (Mismo patrón que SubscriptionController.HireService.)
                var pendingStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Pending.ToStringValue());
                var awaitingStatusId = await GetStatusIdByValueAsync(SearchHireStatus.AwaitingClientDecision.ToStringValue());
                var disputedStatusId = await GetStatusIdByValueAsync(SearchHireStatus.Disputed.ToStringValue());

                var existingHire = await _context.SearchHires
                    .FirstOrDefaultAsync(sh => sh.ClientId == userId &&
                                              sh.SearchServiceId == service.Id &&
                                              (sh.StatusId == pendingStatusId ||
                                               sh.StatusId == awaitingStatusId ||
                                               sh.StatusId == disputedStatusId));

                if (existingHire != null)
                {
                    return BadRequest(new { message = "Ya tienes una contratación activa para este servicio" });
                }

                // ✅ FIX CRÍTICO: NO usar ExecutionStrategy con transacciones manuales en PgBouncer
                // Este método solo crea una sesión de Stripe (operación externa), no necesita transacción
                // Eliminada transacción y ExecutionStrategy para evitar conflictos con PgBouncer
                try
                {
                    // ✅ All payments are now processed through Stripe - no internal balance system
                    var amountToCharge = service.Price;

                    var domain = _configuration["App:FrontendBaseUrl"] ?? "https://inspecciono.com";

                    // 🛡️ Round 28 MUD-7: portado el bloque MUD-6 desde SubscriptionController.HireService.
                    // Antes: Currency hardcoded "eur" en línea 624 → experto US (acct USD) cobraba EUR
                    // → Stripe rechazaba con currency_mismatch o ejecutaba mal el transfer.
                    // CheckoutPage REALMENTE llama a este endpoint (POST /api/Search/create-with-hire),
                    // no a /api/Subscription/hire-service. Por eso MUD-6 antiguo nunca se ejecutaba.
                    // Lógica idéntica: leer service.Currency (snapshot BD) y overriding contra
                    // stripeAccount.DefaultCurrency (verdad autoritativa e inmutable).
                    var checkoutCurrency = (service.Currency ?? "EUR").ToLowerInvariant();
                    if (!string.IsNullOrEmpty(service.ExpertProfile?.StripeAccountId))
                    {
                        try
                        {
                            var stripeAcctService = new Stripe.AccountService();
                            var stripeAcct = await stripeAcctService.GetAsync(service.ExpertProfile.StripeAccountId);
                            var acctDefault = stripeAcct?.DefaultCurrency;
                            if (!string.IsNullOrEmpty(acctDefault) && !string.Equals(acctDefault, checkoutCurrency, StringComparison.OrdinalIgnoreCase))
                            {
                                // 🛡️ BUG #12 FIX: NO reescribir la etiqueta de divisa sin convertir el importe
                                // (eso cobraría 100 USD por un servicio de 100 EUR). La divisa de BD diverge del
                                // default_currency del Stripe acct → dato corrupto. Bloqueamos el cobro (nunca
                                // cobrar mal) y dejamos que el saneo de raíz (reprecio/desactivación del servicio)
                                // lo corrija. NO usar FX automático aquí: ExchangeRateService degrada a 1.0 en
                                // silencio (re-introduciría el bug) y convertir el consentimiento del cliente sin
                                // re-confirmación es incorrecto.
                                await _loggingService.LogCriticalAsync(
                                    message: "CreateSearchWithHire MUD-7: divisa del servicio diverge del Stripe acct — checkout BLOQUEADO",
                                    details: $"Service {service.Id}: BD dice {checkoutCurrency.ToUpperInvariant()}, Stripe acct {service.ExpertProfile.StripeAccountId} default_currency es {acctDefault.ToUpperInvariant()}. Cobro bloqueado para no cobrar en divisa equivocada. ACCIÓN ADMIN: reprecificar/desactivar el servicio (ver endpoint expert-country-divergence).",
                                    userId: userId,
                                    source: "SearchController.CreateSearchWithHire.MUD7",
                                    relatedEntityType: "SearchService",
                                    relatedEntityId: service.Id);
                                return BadRequest(new
                                {
                                    message = "Este servicio no está disponible para contratar en este momento por una incidencia con la moneda de cobro del profesional. Hemos avisado a soporte; inténtalo de nuevo más tarde.",
                                    errorCode = "SERVICE_CURRENCY_MISMATCH"
                                });
                            }
                        }
                        catch (Stripe.StripeException stripeReadEx) when (stripeReadEx.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            return BadRequest(new
                            {
                                message = "Este experto no puede recibir cobros en este momento (cuenta de pagos no disponible). Por favor, contacta al soporte o vuelve a intentar más tarde.",
                                errorCode = "EXPERT_STRIPE_ACCOUNT_NOT_FOUND"
                            });
                        }
                        catch (Exception stripeReadEx)
                        {
                            await _loggingService.LogWarningAsync(
                                message: "CreateSearchWithHire MUD-7: could not verify Stripe acct currency, using BD value",
                                details: $"Service {service.Id}: {stripeReadEx.Message}. Using {checkoutCurrency.ToUpperInvariant()} from BD.",
                                userId: userId,
                                source: "SearchController.CreateSearchWithHire.MUD7",
                                relatedEntityType: "SearchService",
                                relatedEntityId: service.Id);
                        }
                    }

                    // ✅ CHECKOUT UX: line item legible para el comprador (Spanish-first, voz de marca).
                    // Antes: "Payment for Service {id}" — inglés + ID interno, sin contexto ni foto.
                    var checkoutProductName = I6_Truncate(
                        service.ServiceType?.Name?.Trim()
                            ?? service.Category?.Name?.Trim()
                            ?? "Inspección pre-compra",
                        250);
                    var checkoutProductDescription = I6_Truncate(service.Conditions?.Trim(), 300);
                    // Stripe exige URLs https absolutas y públicas; descartamos rutas relativas/no http.
                    var checkoutProductImage = service.Images?
                        .Select(img => img.ImageUrl)
                        .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)
                            && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

                    var checkoutProductData = new SessionLineItemPriceDataProductDataOptions
                    {
                        Name = checkoutProductName
                        // ✅ STRIPE TAX (Docs 2026): NO especificar TaxBehavior para que Stripe use el default automático configurado en Dashboard
                        // Si el Dashboard está en "Automático", Stripe aplicará según moneda: USD/CAD → exclusive, resto → inclusive
                        // Si se especifica, solo se permiten: "inclusive" o "exclusive" (no "unspecified" ni "automatic")
                    };
                    if (!string.IsNullOrWhiteSpace(checkoutProductDescription))
                        checkoutProductData.Description = checkoutProductDescription;
                    if (!string.IsNullOrWhiteSpace(checkoutProductImage))
                        checkoutProductData.Images = new List<string> { checkoutProductImage };

                    // 🌐 Locale del Checkout: primer tag del Accept-Language; "en" si es inglés, "es" por defecto.
                    var acceptLang = Request.Headers["Accept-Language"].ToString();
                    var firstLangTag = acceptLang.Split(',').FirstOrDefault()?.Trim() ?? string.Empty;
                    var checkoutLocale = firstLangTag.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "es";

                    var submitMessage = checkoutLocale == "en"
                        ? "We don't pay the expert until you review the report and approve it. Free cancellation before the review starts."
                        : "No pagamos al experto hasta que revises el informe y des el visto bueno. Cancelación gratuita antes de que empiece la revisión.";
                    var afterSubmitMessage = checkoutLocale == "en"
                        ? "We'll email you at every step. You can track your inspection status in your Inspecciono account."
                        : "Te avisaremos por email en cada paso. Puedes seguir el estado de tu inspección en tu cuenta de Inspecciono.";

                    // 🤝 Coordínalo Inspecciono: si es modo seller, exigir al menos un canal de contacto del
                    // vendedor BIEN FORMADO (email válido O teléfono ES). Sin esto un typo deja el magic-link sin
                    // destinatario real → el vendedor no reserva → auto-cancelación a 48h (caso de uso roto y
                    // silencioso). Defensa en profundidad: el front también valida, pero esto cierra el bypass
                    // (curl/móvil). Normalizamos el teléfono a E.164 (Twilio lo exige) y descartamos el canal
                    // inválido en vez de bloquear si el otro es válido.
                    if (string.Equals(request.CoordinationMode, "seller", StringComparison.OrdinalIgnoreCase))
                    {
                        var sPhone = request.SellerPhone?.Trim();
                        var sEmail = request.SellerEmail?.Trim();
                        var emailOk = !string.IsNullOrEmpty(sEmail)
                            && new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(sEmail);
                        var normalizedPhone = string.IsNullOrEmpty(sPhone)
                            ? null
                            : global::newApi.Services.PhoneLookupService.NormalizeToE164(sPhone, "ES");
                        // El teléfono solo cuenta como canal si puede RECIBIR SMS = móvil. ES (+34): empieza por
                        // 6 o 7 (8/9 son fijos → no reciben SMS). Otros países (+xx): no clasificamos, se acepta
                        // (Twilio decide). Un fijo se descarta como canal; si hay email válido, el email lo cubre.
                        var phoneSmsOk = !string.IsNullOrEmpty(normalizedPhone)
                            && (!normalizedPhone!.StartsWith("+34", StringComparison.Ordinal)
                                || (normalizedPhone!.Length >= 4 && (normalizedPhone![3] == '6' || normalizedPhone![3] == '7')));
                        if (!emailOk && !phoneSmsOk)
                        {
                            return BadRequest(new { error = "Indica un móvil del vendedor que pueda recibir SMS (con su prefijo de país) o un email válido para coordinar la cita." });
                        }
                        // Persistimos solo los canales válidos (móvil ya en E.164); un fijo o número raro se descarta.
                        request.SellerPhone = phoneSmsOk ? normalizedPhone : null;
                        request.SellerEmail = emailOk ? sEmail : null;

                        // 🛡️ Coherencia seller/hueco: en modo seller el hueco lo elige el VENDEDOR por el
                        // enlace, NUNCA el comprador en el checkout. Si llega un startsAtUtc junto a
                        // coordinationMode=="seller" (combinación que el front no produce, pero una llamada
                        // API fabricada sí podría), lo IGNORAMOS: así el webhook entra siempre por la rama del
                        // magic-link al vendedor y nunca se crea una cita seller+hueco sin voz del vendedor.
                        request.StartsAtUtc = null;
                        request.EndsAtUtc = null;
                    }

                    // 🛡️ BUG #4 FIX: normalizar el casing del modo en ORIGEN. El webhook compara
                    // coordModeRaw=="seller" case-sensitive en un punto; un "Seller" (de una llamada API
                    // fabricada) dejaría el hire huérfano (sin token ni enlace al vendedor). El front siempre
                    // manda minúsculas; normalizar aquí lo blinda end-to-end (metadata + persistencia coherentes).
                    request.CoordinationMode = request.CoordinationMode?.Trim().ToLowerInvariant();

                    var options = new SessionCreateOptions
                    {
                        // 💳 Métodos de pago automáticos (dinámicos del Dashboard). Al NO fijar PaymentMethodTypes,
                        // Stripe muestra los métodos habilitados; con CaptureMethod=manual (escrow) FILTRA y deja
                        // solo los compatibles con captura manual: tarjeta + Apple Pay + Google Pay + Link.
                        // SEPA/Bizum/PayPal quedan fuera por diseño (no soportan captura manual → romperían el escrow).
                        // ✅ CHECKOUT UX: botón "Reservar" en la página de Stripe (paridad con el CTA del checkout propio).
                        SubmitType = "book",
                        Locale = checkoutLocale,
                        // 🎨 BRANDING: nombre de negocio + logo/icono + color de marca en la página hospedada.
                        // Logo e Icon son null-safe: si las config keys no están en Render, Stripe las omite.
                        BrandingSettings = new Stripe.Checkout.SessionBrandingSettingsOptions
                        {
                            DisplayName = global::newApi.Services.StripeBranding.InspeccionoBranding.DisplayName,
                            ButtonColor = global::newApi.Services.StripeBranding.InspeccionoBranding.PrimaryColor,
                            FontFamily = "inter", // 🎨 fuente de marca; valor enum de la API en snake_case (NO "Inter")
                            Logo = string.IsNullOrWhiteSpace(_configuration["Stripe:BrandingLogoFileId"]) ? null
                                : new Stripe.Checkout.SessionBrandingSettingsLogoOptions
                                {
                                    Type = "file",
                                    File = _configuration["Stripe:BrandingLogoFileId"]
                                },
                            Icon = string.IsNullOrWhiteSpace(_configuration["Stripe:BrandingIconFileId"]) ? null
                                : new Stripe.Checkout.SessionBrandingSettingsIconOptions
                                {
                                    Type = "file",
                                    File = _configuration["Stripe:BrandingIconFileId"]
                                }
                        },
                        // ✅ CHECKOUT UX: refuerzo de confianza (escrow) justo encima del botón de pago.
                        CustomText = new SessionCustomTextOptions
                        {
                            Submit = new SessionCustomTextSubmitOptions
                            {
                                Message = submitMessage
                            },
                            AfterSubmit = new SessionCustomTextAfterSubmitOptions
                            {
                                Message = afterSubmitMessage
                            }
                        },
                        LineItems = new List<SessionLineItemOptions>
                        {
                            new SessionLineItemOptions
                            {
                                PriceData = new SessionLineItemPriceDataOptions
                                {
                                    // 🛡️ Round 28 MUD-7: usar checkoutCurrency validado contra Stripe acct.
                                    Currency = checkoutCurrency,
                                    UnitAmount = checked((long)Math.Round(amountToCharge * 100)),
                                    ProductData = checkoutProductData
                                },
                                Quantity = 1
                            }
                        },
                        // ✅ STRIPE TAX: Habilitar cálculo automático de tax
                        AutomaticTax = new SessionAutomaticTaxOptions
                        {
                            Enabled = true,
                            Liability = new SessionAutomaticTaxLiabilityOptions { Type = "self" } // 🔧 FIX: plataforma = responsable fiscal (MoR)
                        },
                        TaxIdCollection = new SessionTaxIdCollectionOptions { Enabled = true }, // 🔧 FIX: recoge NIF/VAT -> reverse charge B2B
                        BillingAddressCollection = "required", // 🔧 FIX: direccion fiable para AutomaticTax correcto por pais
                        Mode = "payment",
                        SuccessUrl = $"{domain}/success?session_id={{CHECKOUT_SESSION_ID}}&userId={userId}&serviceId={service.Id}",
                        CancelUrl = $"{domain}/cancel",
                        CustomerEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown@example.com",
                        // 🛡️ I6 FIX: truncar UNA SOLA VEZ y usar los valores truncados para
                        // AMBOS metadata Y idempotency key. Antes el hash usaba valores ORIGINALES
                        // (sin truncar) mientras metadata SÍ truncaba — si el cliente enviaba el
                        // mismo body desde frontend (consistente) funcionaba, pero si bypaseaba
                        // el frontend (curl, mobile sin truncar, refactor frontend) → hash distinto
                        // del esperado → Stripe NO reconocía idempotencia → segunda sesión creada
                        // → doble cobro potencial.
                        Metadata = new Dictionary<string, string>
                        {
                            { "userId", userId.ToString() },
                            { "serviceId", service.Id.ToString() },
                            // 🛡️ A9 FIX: InvariantCulture (consistencia hash idempotency Stripe)
                            { "amount", amountToCharge.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                            { "pendingHire", "true" },
                            // ✅ OPTIMIZACIÓN: Solo enviar campos esenciales para evitar exceder límite de 500 caracteres
                            // En lugar de serializar DTOs completos, enviar solo IDs y campos críticos
                            { "searchId", "0" }, // ✅ CreateSearchDto no tiene Id (se crea después del pago)
                            { "searchTitle", I6_Truncate(searchDto?.Title, 100) },
                            { "searchDescription", I6_Truncate(searchDto?.Description, 100) },
                            { "frequency", searchDto?.Frequency.ToString() ?? "24" },
                            { "keywords", I6_Truncate(parameterDto?.Keywords, 100) },
                            { "userSearch", I6_Truncate(parameterDto?.UserSearch, 200) },
                            { "latitude", parameterDto?.Latitude ?? "" },
                            { "longitude", parameterDto?.Longitude ?? "" },
                            { "locationName", I6_Truncate(parameterDto?.LocationName, 100) },
                            { "categoryId", parameterDto?.Category?.ToString() ?? "" },
                            { "serviceTypeId", parameterDto?.ServiceTypeId?.ToString() ?? "" },
                            { "locationRange", parameterDto?.LocationRange?.ToString() ?? "" },
                            // 🗓️ Reserva atómica: hueco elegido en UTC (ISO 8601 round-trip). Vacío si el
                            // servicio no usa cita. El webhook lo lee para crear la cita YA CONFIRMADA y
                            // dejar que la exclusion constraint GiST garantice que no hay doble-booking.
                            { "startsAtUtc", request.StartsAtUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "" },
                            { "endsAtUtc", request.EndsAtUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "" },
                            // 🗓️ Fase E: ubicación de la cita (servicios con radio). El webhook la pone en el Appointment.
                            { "apptLocation", I6_Truncate(request.Location, 150) },
                            { "apptLat", request.Latitude ?? "" },
                            { "apptLng", request.Longitude ?? "" },
                            { "apptDoor", I6_Truncate(request.DoorNumber, 60) },
                            { "apptDetails", I6_Truncate(request.SiteDetails, 200) },
                            // 🤝 Coordinación con el vendedor (modo "seller"): el webhook guarda estos
                            // datos en el hire. Vacíos en el modo "self" (el cliente eligió hueco).
                            { "coordinationMode", I6_Truncate(request.CoordinationMode, 16) },
                            { "sellerPhone", I6_Truncate(request.SellerPhone, 30) },
                            // BUG #5 FIX: 254 (máx RFC de email), no 60. Stripe permite 500 POR VALOR (no es un
                            // presupuesto total), así que cabe. 60 cortaba emails corporativos válidos → el magic-link
                            // iba a una dirección recortada (rebote o buzón equivocado) → cita sin reservar.
                            { "sellerEmail", I6_Truncate(request.SellerEmail, 254) },
                            // 🛡️ FIX [W3-LISTING-LEN] (auditoría 2026-07-12): 500 (máx de Stripe por valor), no 120.
                            // Los anuncios reales (coches.net/Autoscout/Wallapop) superan 120 chars con facilidad y la
                            // URL truncada se persistía y se servía ROTA en la página del vendedor (listingUrl).
                            { "sellerListing", I6_Truncate(request.SellerListingUrl, 500) },
                            { "sellerMaxDays", request.SellerBookingMaxDays?.ToString() ?? "" }
                        },
                        // ✅ CAPTURA MANUAL: Autoriza el pago pero no lo captura hasta validar todo en el webhook
                        // Esto evita perder comisiones si algo falla después del pago
                        PaymentIntentData = new SessionPaymentIntentDataOptions
                        {
                            CaptureMethod = "manual"
                        }
                    };

                    var serviceStripe = new SessionService();
                    // 🔧 FIX #6 + regresión: clave determinista con HASH del body. La versión "-none" era estable
                    // 24h por (usuario,servicio), pero el body lleva metadata POR-BÚSQUEDA; dos inspecciones
                    // DISTINTAS del mismo servicio en <24h => body distinto, misma clave => Stripe idempotency_error
                    // (400) => 500 al cliente. Con el hash: misma búsqueda (doble-clic) => misma clave (deduplica,
                    // cierra el doble cobro); búsqueda distinta => clave distinta (deja contratar).
                    // 🛡️ I6 FIX: usar valores TRUNCADOS (mismos que metadata) para el hash de
                    // idempotency. Esto garantiza que el doble-click rápido genera la MISMA key
                    // aunque el frontend envíe valores ligeramente distintos (variaciones de
                    // truncación, normalización Unicode, etc.). Antes el hash usaba originales →
                    // si el segundo request truncaba antes o difería en 1 char post-100, key
                    // distinta → Stripe creaba 2 sesiones.
                    var idempotencyKey = IdempotencyKeyHelper.ForCheckout(
                        userId, service.Id,
                        amountToCharge.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        I6_Truncate(searchDto?.Title, 100),
                        I6_Truncate(searchDto?.Description, 100),
                        I6_Truncate(parameterDto?.Keywords, 100),
                        I6_Truncate(parameterDto?.UserSearch, 200),
                        parameterDto?.Latitude, parameterDto?.Longitude,
                        I6_Truncate(parameterDto?.LocationName, 100),
                        parameterDto?.Category?.ToString(), parameterDto?.ServiceTypeId?.ToString(),
                        searchDto?.Frequency.ToString(), parameterDto?.LocationRange?.ToString(),
                        // 🔧 FIX: hueco/cita y modo vendedor TAMBIÉN van en el body (metadata) → deben
                        // discriminar la clave; si no, reiniciar checkout del mismo servicio+búsqueda con
                        // otro hueco o en modo seller reusa la clave con body distinto → idempotency_error
                        // (400) → 500 → reserva legítima denegada. Mismo hueco+datos = misma clave (sigue
                        // deduplicando el doble-submit del MISMO checkout).
                        request.StartsAtUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                        request.EndsAtUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                        I6_Truncate(request.Location, 150),
                        request.Latitude, request.Longitude,
                        I6_Truncate(request.DoorNumber, 60),
                        I6_Truncate(request.SiteDetails, 200),
                        I6_Truncate(request.CoordinationMode, 16),
                        I6_Truncate(request.SellerPhone, 30),
                        I6_Truncate(request.SellerEmail, 254), // BUG #5: mismo límite que el metadata (arriba) o el hash de idempotencia diverge
                        I6_Truncate(request.SellerListingUrl, 120),
                        request.SellerBookingMaxDays?.ToString()); // 🔧 FIX: frequency y locationRange también van en el body → deben discriminar la clave (si no, idempotency_error 400)
                    var session = await serviceStripe.CreateAsync(options, new RequestOptions { IdempotencyKey = idempotencyKey });

                    await _loggingService.LogInfoAsync(
                        message: "Sesión de pago Stripe creada exitosamente para búsqueda con contratación",
                        details: $"SessionId: {session.Id}, ServiceId: {service.Id}, ExpertId: {service.ExpertProfile?.UserId}, UserId: {userId}",
                        userId: userId,
                        source: "SearchController.CreateSearchWithHire",
                        relatedEntityType: "Payment",
                        relatedEntityId: null,
                        additionalData: new { 
                            SessionId = session.Id,
                            ServiceId = service.Id,
                            ExpertId = service.ExpertProfile?.UserId
                        }
                    );

                    return Ok(new { url = session.Url });
                }
                catch (StripeException ex)
                {
                    await _loggingService.LogErrorAsync(
                        message: "Error Stripe al crear búsqueda con contratación",
                        details: $"StripeException al crear checkout session. ServiceId: {service?.Id}, ExpertId: {service?.ExpertProfile?.UserId}, UserId: {userId}, Error: {ex.Message}, StripeError: {ex.StripeError?.Message}",
                        userId: userId,
                        source: "SearchController.CreateSearchWithHire",
                        relatedEntityType: "Payment",
                        relatedEntityId: null,
                        additionalData: new { 
                            ServiceId = service?.Id,
                            ExpertId = service?.ExpertProfile?.UserId,
                            StripeErrorType = ex.StripeError?.Type,
                            StripeErrorCode = ex.StripeError?.Code,
                            StripeErrorMessage = ex.StripeError?.Message
                        }
                    );

                    return StatusCode(500, new { message = "No se pudo procesar el pago en este momento. Inténtalo de nuevo más tarde.", errorCode = "STRIPE_CHECKOUT_ERROR" });
                }
                catch (Exception ex)
                {
                    await _loggingService.LogErrorAsync(
                        message: "Error al crear búsqueda con contratación",
                        details: $"Error en CreateSearchWithHire. ServiceId: {service?.Id}, ExpertId: {service?.ExpertProfile?.UserId}, UserId: {userId}, Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                        userId: userId,
                        source: "SearchController.CreateSearchWithHire",
                        relatedEntityType: "Search",
                        relatedEntityId: null,
                        additionalData: new { 
                            ServiceId = service?.Id,
                            ExpertId = service?.ExpertProfile?.UserId,
                            ErrorType = ex.GetType().Name,
                            ErrorMessage = ex.Message,
                            StackTrace = ex.StackTrace,
                            InnerException = ex.InnerException?.Message
                        }
                    );

                    return StatusCode(500, new { message = "Ha ocurrido un error al crear la contratación.", errorCode = "CREATE_SEARCH_WITH_HIRE_ERROR" });
                }
            }
            catch (Exception ex)
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                int? userId = null;
                if (int.TryParse(userIdClaim, out int parsedUserId))
                {
                    userId = parsedUserId;
                }

                await _loggingService.LogErrorAsync(
                    message: "Error general al crear búsqueda con contratación",
                    details: $"Error general en CreateSearchWithHire. UserId: {userId}, ServiceId: {request?.SearchDto?.ServiceId}, Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId,
                    source: "SearchController.CreateSearchWithHire",
                    relatedEntityType: "Search",
                    relatedEntityId: null,
                    additionalData: new { 
                        ServiceId = request?.SearchDto?.ServiceId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                return StatusCode(500, new { message = "Ha ocurrido un error al crear la contratación.", errorCode = "CREATE_SEARCH_WITH_HIRE_ERROR" });
            }
        }

        [HttpPut("{searchId}/revise")]
        public async Task<IActionResult> MarkAsRevised(int searchId)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                if (!_authService.IsAdmin(User))
                {
                    return Unauthorized(new { message = "Admin access required" });
                }

                var search = await _context.Searches.FindAsync(searchId);
                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                search.IsRevised = true;
                await _context.SaveChangesAsync();

                await _loggingService.LogInfoAsync(
                    message: "Búsqueda marcada como revisada",
                    details: $"Search {searchId} marcada como revisada por usuario {userId}",
                    userId: userId,
                    source: "SearchController.MarkAsRevised",
                    relatedEntityType: "Search",
                    relatedEntityId: searchId
                );

                return Ok(new { message = "Search marked as revised" });
            }
            catch (Exception ex)
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                int? userId = null;
                if (int.TryParse(userIdClaim, out int parsedUserId))
                {
                    userId = parsedUserId;
                }

                await _loggingService.LogErrorAsync(
                    message: "Error al marcar búsqueda como revisada",
                    details: $"Error marcando Search {searchId} como revisada. UserId: {userId}, Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId,
                    source: "SearchController.MarkSearchAsRevised",
                    relatedEntityType: "Search",
                    relatedEntityId: searchId,
                    additionalData: new { 
                        SearchId = searchId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                return StatusCode(500, new { message = "Ha ocurrido un error al marcar la búsqueda como revisada.", errorCode = "MARK_REVISED_ERROR" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> CreateSearch([FromBody] CreateSearchDto searchDto)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var activeSearchCount = await _context.Searches.CountAsync(s => s.UserId == userId && s.IsActive);

                var user = await _userService.GetUserAsync(userId);
                if (user == null)
                {
                    return NotFound(new { message = "User not found" });
                }

                // ✅ VALIDACIÓN: Usuario bloqueado no puede crear búsquedas
                if (user.IsBlocked)
                {
                    await _loggingService.LogWarningAsync(
                        message: "Blocked user attempted to create search",
                        details: $"Blocked user {user.Id} ({user.Email}) attempted to create a search",
                        userId: user.Id,
                        source: "SearchController.CreateSearch",
                        relatedEntityType: "User",
                        relatedEntityId: user.Id
                    );
                    return Unauthorized(new { message = "User account is blocked" });
                }

                // 📱 La verificación de móvil/SMS NO se exige al CLIENTE para contratar. Solo los
                // EXPERTOS deben verificar su móvil (para ser visibles en el catálogo, gate en
                // SearchServiceService + /api/User/expert-visibility). El cliente puede contratar sin
                // móvil verificado; si tiene un móvil (capturado en checkout/perfil) recibirá los SMS,
                // pero no es obligatorio.

                var search = new Search
                {
                    UserId = userId,
                    Frequency = searchDto.Frequency,
                    Title = searchDto.Title,
                    Description = searchDto.Description,
                    IsActive = searchDto.IsActive,
                    NextExecution = DateTime.UtcNow,
                    StartDate = searchDto.StartDate,
                    CreatedAt = DateTime.UtcNow
                };

                await _context.Searches.AddAsync(search);
                await _context.SaveChangesAsync();

                await _loggingService.LogInfoAsync(
                    message: "Búsqueda creada exitosamente",
                    details: $"Search {search.Id} creada por usuario {userId}. Title: {searchDto.Title}, Frequency: {searchDto.Frequency}",
                    userId: userId,
                    source: "SearchController.CreateSearch",
                    relatedEntityType: "Search",
                    relatedEntityId: search.Id
                );

                return Ok(new { search.Id });
            }
            catch (Exception ex)
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                int? userId = null;
                if (int.TryParse(userIdClaim, out int parsedUserId))
                {
                    userId = parsedUserId;
                }

                await _loggingService.LogErrorAsync(
                    message: "Error al crear búsqueda",
                    details: $"Error creando búsqueda. UserId: {userId}, Title: {searchDto?.Title}, Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId,
                    source: "SearchController.CreateSearch",
                    relatedEntityType: "Search",
                    relatedEntityId: null,
                    additionalData: new { 
                        Title = searchDto?.Title,
                        Description = searchDto?.Description,
                        Frequency = searchDto?.Frequency,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                return StatusCode(500, new { message = "Ha ocurrido un error al crear la búsqueda.", errorCode = "CREATE_SEARCH_ERROR" });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetUserSearches([FromQuery] UserSearchListRequestDto request)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                if (request.Page < 1) request.Page = 1;
                if (request.PageSize < 1 || request.PageSize > 50) request.PageSize = 20;

                var query = _context.Searches
                    .Where(s => s.UserId == userId)
                    .Include(s => s.User)
                    .Include(s => s.SearchParameters)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                        .ThenInclude(e => e.ExpertProfile)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Conversations)
                        .ThenInclude(c => c.Messages)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Appointment)
                    .AsQueryable();

                if (!string.IsNullOrEmpty(request.SearchTerm))
                {
                    var searchTerm = request.SearchTerm.ToLower();
                    query = query.Where(s => 
                        s.Title.ToLower().Contains(searchTerm) || 
                        s.Description.ToLower().Contains(searchTerm));
                }

                if (request.Category.HasValue)
                {
                    query = query.Where(s => s.SearchParameters.Any(sp => sp.Category == request.Category.Value));
                }

                if (request.IsActive.HasValue)
                {
                    query = query.Where(s => s.IsActive == request.IsActive.Value);
                }

                if (request.IsRevised.HasValue)
                {
                    query = query.Where(s => s.IsRevised == request.IsRevised.Value);
                }

                if (!string.IsNullOrEmpty(request.SearchHireStatus))
                {
                    var statusId = await GetStatusIdByValueAsync(request.SearchHireStatus);
                    query = query.Where(s => s.SearchHire != null && s.SearchHire.StatusId == statusId);
                }

                var totalCount = await query.CountAsync();
                query = ApplySorting(query, request.SortBy, request.SortDirection);

                var searches = await query
                    .Skip((request.Page - 1) * request.PageSize)
                    .Take(request.PageSize)
                    .ToListAsync();

                var searchDtos = searches.Select(s =>
                {
                    if (s.User == null)
                    {
                        throw new InvalidOperationException($"Search {s.Id} has no associated user");
                    }

                    return new SearchListDto
                    {
                        Id = s.Id,
                        UserId = s.UserId,
                        Title = s.Title,
                        Description = s.Description,
                        Frequency = s.Frequency,
                        IsActive = s.IsActive,
                        IsRevised = s.IsRevised,
                        LastExecution = s.LastExecution,
                        CreatedAt = s.CreatedAt,
                        StartDate = s.StartDate,
                        LocationName = s.SearchParameters.FirstOrDefault()?.LocationName,
                        Category = s.SearchParameters.FirstOrDefault()?.Category ?? 0,
                        User = new UserDto
                        {
                            Email = s.User.Email,
                            Name = s.User.Name
                        },
                        SearchHire = s.SearchHire != null ? new SearchHireDto
                        {
                            Id = s.SearchHire.Id,
                            ExpertId = s.SearchHire.ExpertId ?? 0,
                            Status = s.SearchHire.Status.StatusValue,
                            StatusTranslated = SearchHireStatusExtensions.ToSpanishTranslation(s.SearchHire.Status.StatusValue),
                            CreatedAt = s.SearchHire.CreatedAt,
                            Amount = s.SearchHire.Amount, // ✅ STRIPE TAX: Monto total con IVA
                            BaseAmount = s.SearchHire.BaseAmount, // ✅ STRIPE TAX: Base sin IVA
                            TaxAmount = s.SearchHire.TaxAmount, // ✅ STRIPE TAX: IVA calculado
                            // Round 24: snapshot currency del cargo real (Round 21).
                            ChargeCurrency = string.IsNullOrEmpty(s.SearchHire.Currency) ? "EUR" : s.SearchHire.Currency,
                            ExpertTimezone = s.SearchHire.ExpertTimezone, // ✅ INTERNACIONALIZACIÓN
                            ExpertCountry = s.SearchHire.ExpertCountry, // ✅ INTERNACIONALIZACIÓN
                            // 🛡️ Round 10 — P-C FIX (V8 snapshots): exponer al frontend
                            ClientPercentageSnapshot = s.SearchHire.ClientPercentageSnapshot,
                            ExpertPercentageSnapshot = s.SearchHire.ExpertPercentageSnapshot,
                            PlatformPercentageSnapshot = s.SearchHire.PlatformPercentageSnapshot,
                              Expert = s.SearchHire.Expert != null ? new UserDto
                              {
                                  Name = s.SearchHire.Expert.Name,
                                  ProfilePictureUrl = ResolveProfilePictureUrl(s.SearchHire.Expert.ExpertProfile)
                              } : null,
                            // ✅ NUEVO: Información completa del estado con colores
                            StatusInfo = s.SearchHire.Status != null ? new SystemStatusDto
                            {
                                Id = s.SearchHire.Status.Id,
                                StatusType = s.SearchHire.Status.StatusType,
                                StatusName = s.SearchHire.Status.StatusName,
                                StatusValue = s.SearchHire.Status.StatusValue,
                                DisplayName = s.SearchHire.Status.DisplayName,
                                Description = s.SearchHire.Status.Description,
                                Color = s.SearchHire.Status.Color,
                                IsActive = s.SearchHire.Status.IsActive,
                                IsFinalizationStatus = s.SearchHire.Status.IsFinalizationStatus,
                                SortOrder = s.SearchHire.Status.SortOrder,
                                CreatedAt = s.SearchHire.Status.CreatedAt,
                                UpdatedAt = s.SearchHire.Status.UpdatedAt
                            } : null
                        } : null
                    };
                }).ToList();

                foreach (var searchDto in searchDtos)
                {
                    var search = searches.First(s => s.Id == searchDto.Id);
                    
                    if (search.SearchHire != null)
                    {
                        if (search.SearchHire.Conversations != null && search.SearchHire.Conversations.Any())
                        {
                            searchDto.UnreadMessagesCount = search.SearchHire.Conversations
                                .SelectMany(c => c.Messages)
                                .Count(m => m.SenderId != userId && !m.IsRead);
                        }

                        if (search.SearchHire.Appointment != null && search.SearchHire.Appointment.Status != null)
                        {
                            var pendingStatuses = new[] { 
                                "awaiting_appointment", 
                                "appointment_proposed", 
                                "appointment_confirmed" 
                            };
                            
                            if (pendingStatuses.Contains(search.SearchHire.Appointment.Status.StatusValue))
                            {
                                searchDto.HasPendingAppointment = true;
                                searchDto.PendingAppointmentStatus = search.SearchHire.Appointment.Status.StatusValue;
                            }
                        }
                    }
                }

                var userStats = await CalculateUserSearchStats(userId);

                var response = new UserSearchListResponseDto
                {
                    Searches = searchDtos,
                    Pagination = new PaginationMetadata
                    {
                        CurrentPage = request.Page,
                        PageSize = request.PageSize,
                        TotalCount = totalCount,
                        TotalPages = (int)Math.Ceiling((double)totalCount / request.PageSize),
                        HasPrevious = request.Page > 1,
                        HasNext = request.Page < (int)Math.Ceiling((double)totalCount / request.PageSize)
                    },
                    Stats = userStats
                };

                await _loggingService.LogInfoAsync(
                    message: "Lista de búsquedas del usuario obtenida exitosamente",
                    details: $"Usuario {userId} obtuvo {searchDtos.Count} búsquedas (página {request.Page}). Total: {totalCount}",
                    userId: userId,
                    source: "SearchController.GetUserSearches",
                    relatedEntityType: "Search",
                    relatedEntityId: null
                );

                return Ok(response);
            }
            catch (Exception ex)
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                int? userId = null;
                if (int.TryParse(userIdClaim, out int parsedUserId))
                {
                    userId = parsedUserId;
                }

                await _loggingService.LogErrorAsync(
                    message: "Error al obtener lista de búsquedas del usuario",
                    details: $"Error obteniendo búsquedas para usuario {userId}. Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId,
                    source: "SearchController.GetUserSearches",
                    relatedEntityType: "Search",
                    relatedEntityId: null,
                    additionalData: new { 
                        Page = request?.Page,
                        PageSize = request?.PageSize,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                return StatusCode(500, new { message = "Ha ocurrido un error al obtener las búsquedas.", errorCode = "GET_USER_SEARCHES_ERROR" });
            }
        }

        private async Task<UserSearchStats> CalculateUserSearchStats(int userId)
        {
            var userSearches = _context.Searches
                .Where(s => s.UserId == userId)
                .Include(s => s.SearchHire)
                    .ThenInclude(sh => sh.Status)
                .Include(s => s.SearchHire)
                    .ThenInclude(sh => sh.Conversations)
                    .ThenInclude(c => c.Messages)
                .Include(s => s.SearchHire)
                    .ThenInclude(sh => sh.Appointment)
                .AsQueryable();

            var activeSearches = await userSearches.CountAsync(s => s.IsActive);
            var inactiveSearches = await userSearches.CountAsync(s => !s.IsActive);
            var searchesWithHire = await userSearches.CountAsync(s => s.SearchHire != null);
            var searchesWithoutHire = await userSearches.CountAsync(s => s.SearchHire == null);

            var unreadMessages = await userSearches
                .Where(s => s.SearchHire != null)
                .SelectMany(s => s.SearchHire.Conversations)
                .SelectMany(c => c.Messages)
                .CountAsync(m => m.SenderId != userId && !m.IsRead);

            var pendingAppointments = await userSearches
                .Where(s => s.SearchHire != null && s.SearchHire.Appointment != null && s.SearchHire.Appointment.Status != null)
                .CountAsync(s => new[] { 
                    "awaiting_appointment", 
                    "appointment_proposed", 
                    "appointment_confirmed" 
                }.Contains(s.SearchHire.Appointment.Status.StatusValue));

            return new UserSearchStats
            {
                ActiveSearches = activeSearches,
                InactiveSearches = inactiveSearches,
                SearchesWithHire = searchesWithHire,
                SearchesWithoutHire = searchesWithoutHire,
                UnreadMessages = unreadMessages,
                PendingAppointments = pendingAppointments
            };
        }

        [HttpPut("{searchId}/toggle-active")]
        public async Task<IActionResult> ToggleSearchActive(int searchId)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var search = await _context.Searches
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .FirstOrDefaultAsync(s => s.Id == searchId && (s.UserId == userId || _authService.IsAdmin(User)));

                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                if (search.SearchHire != null && !new[] { "pending", "awaiting_client_decision" }.Contains(search.SearchHire.Status.StatusValue))
                {
                    return BadRequest(new { message = "No se puede modificar el estado de una búsqueda finalizada" });
                }

                search.IsActive = !search.IsActive;
                await _context.SaveChangesAsync();

                await _loggingService.LogInfoAsync(
                    message: "Estado de búsqueda actualizado",
                    details: $"Search {searchId} estado cambiado a IsActive={search.IsActive} por usuario {userId}",
                    userId: userId,
                    source: "SearchController.ToggleSearchActive",
                    relatedEntityType: "Search",
                    relatedEntityId: searchId
                );

                return Ok(new { isActive = search.IsActive });
            }
            catch (Exception ex)
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                int? userId = null;
                if (int.TryParse(userIdClaim, out int parsedUserId))
                {
                    userId = parsedUserId;
                }

                await _loggingService.LogErrorAsync(
                    message: "Error al cambiar estado de búsqueda",
                    details: $"Error cambiando estado de Search {searchId}. UserId: {userId}, Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId,
                    source: "SearchController.ToggleSearchActive",
                    relatedEntityType: "Search",
                    relatedEntityId: searchId,
                    additionalData: new { 
                        SearchId = searchId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                return StatusCode(500, new { message = "Ha ocurrido un error al cambiar el estado de la búsqueda.", errorCode = "TOGGLE_SEARCH_ACTIVE_ERROR" });
            }
        }

        [HttpPut("{searchId}")]
        public async Task<IActionResult> UpdateSearch(int searchId, [FromBody] UpdateSearchDto updateDto)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var search = await _context.Searches.FirstOrDefaultAsync(s =>
                    s.Id == searchId && (s.UserId == userId || _authService.IsAdmin(User)));

                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                search.Title = updateDto.Title;
                search.Description = updateDto.Description;
                search.StartDate = updateDto.StartDate;

                var subscriptionLimits = await _subscriptionService.GetUserSubscriptionLimits(userId);
                if (updateDto.Frequency < subscriptionLimits.MinSearchInterval)
                {
                    return StatusCode(403, new { message = $"Minimum search interval for your plan is {subscriptionLimits.MinSearchInterval} hours" });
                }
                search.Frequency = updateDto.Frequency;

                await _context.SaveChangesAsync();

                await _loggingService.LogInfoAsync(
                    message: "Búsqueda actualizada exitosamente",
                    details: $"Search {searchId} actualizada por usuario {userId}",
                    userId: userId,
                    source: "SearchController.UpdateSearch",
                    relatedEntityType: "Search",
                    relatedEntityId: searchId
                );

                return Ok(new { message = "Search updated successfully" });
            }
            catch (Exception ex)
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                int? userId = null;
                if (int.TryParse(userIdClaim, out int parsedUserId))
                {
                    userId = parsedUserId;
                }

                await _loggingService.LogErrorAsync(
                    message: "Error al actualizar búsqueda",
                    details: $"Error actualizando Search {searchId}. UserId: {userId}, Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId,
                    source: "SearchController.UpdateSearch",
                    relatedEntityType: "Search",
                    relatedEntityId: searchId,
                    additionalData: new { 
                        SearchId = searchId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                return StatusCode(500, new { message = "Ha ocurrido un error al actualizar la búsqueda.", errorCode = "UPDATE_SEARCH_ERROR" });
            }
        }

        [HttpGet("{searchId}")]
        public async Task<IActionResult> GetSearch(int searchId)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var search = await _context.Searches
                    .Include(s => s.User)
                    .Include(s => s.SearchParameters)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                        .ThenInclude(e => e.ExpertProfile)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                        .ThenInclude(st => st.ServiceTypeCategory)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Conversations)
                        .ThenInclude(c => c.Messages)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Appointment)
                    .FirstOrDefaultAsync(s => s.Id == searchId &&
                        (s.UserId == userId ||
                         _authService.IsAdmin(User) ||
                         (s.SearchHire != null && s.SearchHire.ExpertId == userId)));

                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                if (search.User == null)
                {
                    return StatusCode(500, new { message = "Search has no associated user" });
                }

                var searchDto = new SearchListDto
                {
                    Id = search.Id,
                    UserId = search.UserId,
                    Title = search.Title,
                    Description = search.Description,
                    Frequency = search.Frequency,
                    IsActive = search.IsActive,
                    IsRevised = search.IsRevised,
                    LastExecution = search.LastExecution,
                    CreatedAt = search.CreatedAt,
                    StartDate = search.StartDate,
                    LocationName = search.SearchParameters.FirstOrDefault()?.LocationName,
                    Category = search.SearchParameters.FirstOrDefault()?.Category ?? 0,
                    User = new UserDto
                    {
                        Email = search.User.Email,
                        Name = search.User.Name
                    },
                    SearchHire = search.SearchHire != null ? new SearchHireDto
                    {
                        Id = search.SearchHire.Id,
                        ExpertId = search.SearchHire.ExpertId ?? 0,
                        Status = search.SearchHire.Status.StatusValue,
                        StatusTranslated = SearchHireStatusExtensions.ToSpanishTranslation(search.SearchHire.Status.StatusValue),
                        CreatedAt = search.SearchHire.CreatedAt,
                        Amount = search.SearchHire.Amount, // ✅ STRIPE TAX: Monto total con IVA
                        BaseAmount = search.SearchHire.BaseAmount, // ✅ STRIPE TAX: Base sin IVA
                        TaxAmount = search.SearchHire.TaxAmount, // ✅ STRIPE TAX: IVA calculado
                        // Round 24: snapshot currency del cargo real (Round 21).
                        ChargeCurrency = string.IsNullOrEmpty(search.SearchHire.Currency) ? "EUR" : search.SearchHire.Currency,
                        ExpertTimezone = search.SearchHire.ExpertTimezone, // ✅ INTERNACIONALIZACIÓN
                        ExpertCountry = search.SearchHire.ExpertCountry, // ✅ INTERNACIONALIZACIÓN
                        // 🛡️ Round 10 — P-C FIX (V8 snapshots): exponer al frontend
                        ClientPercentageSnapshot = search.SearchHire.ClientPercentageSnapshot,
                        ExpertPercentageSnapshot = search.SearchHire.ExpertPercentageSnapshot,
                        PlatformPercentageSnapshot = search.SearchHire.PlatformPercentageSnapshot,
                          Expert = search.SearchHire.Expert != null ? new UserDto
                          {
                              Name = search.SearchHire.Expert.Name,
                              ProfilePictureUrl = ResolveProfilePictureUrl(search.SearchHire.Expert.ExpertProfile)
                          } : null,
                        Service = search.SearchHire.SearchService != null ? new ServiceInfo
                        {
                            Id = search.SearchHire.SearchService.Id,
                            ServiceTypeId = search.SearchHire.SearchService.ServiceTypeId,
                            ServiceTypeName = search.SearchHire.SearchService.ServiceType?.Name ?? "Unknown Service Type",
                            ServiceTypeCategoryId = search.SearchHire.SearchService.ServiceType?.ServiceTypeCategoryId,
                            ServiceTypeCategoryName = search.SearchHire.SearchService.ServiceType?.ServiceTypeCategory?.Name,
                            RequiresAppointment = search.SearchHire.SearchService.ServiceType?.RequiresAppointment ?? false,
                            // 🛡️ SNAPSHOT CONTRACTUAL: precio contratado (BaseAmount del hire), no el actual.
                            Price = search.SearchHire.BaseAmount ?? search.SearchHire.SearchService.Price,
                            Conditions = search.SearchHire.ConditionsSnapshot ?? search.SearchHire.SearchService.Conditions,
                            DurationInHours = search.SearchHire.DurationInHoursSnapshot ?? search.SearchHire.SearchService.DurationInHours,
                            // Round 24: poblar Currency (default 'EUR' si null).
                            Currency = string.IsNullOrEmpty(search.SearchHire.SearchService.Currency) ? "EUR" : search.SearchHire.SearchService.Currency,
                            // ✅ NUEVOS CAMPOS: Información de ubicación del experto
                            // 🛡️ SNAPSHOT CONTRACTUAL: ubicación/radio del experto AL CONTRATAR.
                            ExpertLatitude = search.SearchHire.ExpertLatitudeSnapshot ?? search.SearchHire.SearchService.ExpertProfile?.Latitude,
                            ExpertLongitude = search.SearchHire.ExpertLongitudeSnapshot ?? search.SearchHire.SearchService.ExpertProfile?.Longitude,
                            LocationRange = search.SearchParameters?.FirstOrDefault()?.LocationRange ?? 50, // Rango por defecto de 50km
                            ExpertWorkRadiusKm = search.SearchHire.ExpertWorkRadiusKmSnapshot ?? search.SearchHire.SearchService.ExpertProfile?.WorkRadiusKm
                        } : null,
                        // ✅ NUEVO: Información completa del estado con colores
                        StatusInfo = search.SearchHire.Status != null ? new SystemStatusDto
                        {
                            Id = search.SearchHire.Status.Id,
                            StatusType = search.SearchHire.Status.StatusType,
                            StatusName = search.SearchHire.Status.StatusName,
                            StatusValue = search.SearchHire.Status.StatusValue,
                            DisplayName = search.SearchHire.Status.DisplayName,
                            Description = search.SearchHire.Status.Description,
                            Color = search.SearchHire.Status.Color,
                            IsActive = search.SearchHire.Status.IsActive,
                            IsFinalizationStatus = search.SearchHire.Status.IsFinalizationStatus,
                            SortOrder = search.SearchHire.Status.SortOrder,
                            CreatedAt = search.SearchHire.Status.CreatedAt,
                            UpdatedAt = search.SearchHire.Status.UpdatedAt
                        } : null
                    } : null
                };

                if (search.SearchHire != null)
                {
                    if (search.SearchHire.Conversations != null && search.SearchHire.Conversations.Any())
                    {
                        searchDto.UnreadMessagesCount = search.SearchHire.Conversations
                            .SelectMany(c => c.Messages)
                            .Count(m => m.SenderId != userId && !m.IsRead);
                    }

                    if (search.SearchHire.Appointment != null && search.SearchHire.Appointment.Status != null)
                    {
                        var pendingStatuses = new[] { 
                            "awaiting_appointment", 
                            "appointment_proposed", 
                            "appointment_confirmed" 
                        };
                        
                        if (pendingStatuses.Contains(search.SearchHire.Appointment.Status.StatusValue))
                        {
                            searchDto.HasPendingAppointment = true;
                            searchDto.PendingAppointmentStatus = search.SearchHire.Appointment.Status.StatusValue;
                        }
                    }
                }

                await _loggingService.LogInfoAsync(
                    message: "Búsqueda obtenida exitosamente",
                    details: $"Search {searchId} obtenida por usuario {userId}",
                    userId: userId,
                    source: "SearchController.GetSearch",
                    relatedEntityType: "Search",
                    relatedEntityId: searchId
                );

                return Ok(searchDto);
            }
            catch (Exception ex)
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                int? userId = null;
                if (int.TryParse(userIdClaim, out int parsedUserId))
                {
                    userId = parsedUserId;
                }

                await _loggingService.LogErrorAsync(
                    message: "Error al obtener búsqueda",
                    details: $"Error obteniendo Search {searchId}. UserId: {userId}, Error: {ex.GetType().Name} - {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: userId,
                    source: "SearchController.GetSearch",
                    relatedEntityType: "Search",
                    relatedEntityId: searchId,
                    additionalData: new { 
                        SearchId = searchId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );

                return StatusCode(500, new { message = "Ha ocurrido un error al obtener la búsqueda.", errorCode = "GET_SEARCH_ERROR" });
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

        private string ResolveDeliverableUrl(SearchHireDeliverable? deliverable)
        {
            if (deliverable == null)
            {
                return string.Empty;
            }

            var fallback = string.IsNullOrWhiteSpace(deliverable.Url) ? string.Empty : deliverable.Url;
            return _signedUrlService.GetSignedUrl(deliverable.ObjectName ?? string.Empty) ?? fallback;
        }
    }

    /// <summary>
    /// DTO para crear una búsqueda con contratación.
    /// </summary>
    public class CreateSearchWithHireDto
    {
        /// <summary>
        /// DTO de la búsqueda a crear.
        /// </summary>
        public CreateSearchDto SearchDto { get; set; }
        
        /// <summary>
        /// DTO con los parámetros de la búsqueda.
        /// </summary>
        public CreateSearchParameterDto ParameterDto { get; set; }

        /// <summary>
        /// 🗓️ Reserva atómica (Calendly): inicio del hueco de cita elegido por el cliente, en UTC.
        /// Null si el servicio no requiere cita. Se asegura en el webhook con la exclusion constraint GiST.
        /// </summary>
        public DateTime? StartsAtUtc { get; set; }

        /// <summary>Fin del hueco elegido, en UTC (= inicio + duración del servicio).</summary>
        public DateTime? EndsAtUtc { get; set; }

        /// <summary>
        /// 🗓️ Fase E: ubicación de la cita elegida por el cliente (servicios con radio de km).
        /// Para servicios estáticos (taller, radio 0) es la ubicación del experto. Nullable.
        /// </summary>
        public string? Location { get; set; }
        public string? Latitude { get; set; }
        public string? Longitude { get; set; }
        public string? DoorNumber { get; set; }
        public string? SiteDetails { get; set; }

        // 🤝 Coordinación con el vendedor (modo "seller"). Vacío/null en el modo "self".
        /// <summary>"self" (el cliente eligió hueco) o "seller" (el experto propondrá tras coordinar).</summary>
        public string? CoordinationMode { get; set; }
        public string? SellerPhone { get; set; }
        public string? SellerEmail { get; set; }
        public string? SellerListingUrl { get; set; }
        /// <summary>Modo seller: máximo de días a futuro que el vendedor puede elegir la cita.</summary>
        public int? SellerBookingMaxDays { get; set; }
    }
}