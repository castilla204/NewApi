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
        private readonly AppDbContext _context;
        private readonly IAuthorizationServices _authService;
        private readonly IUserService _userService;
        private readonly ISubscriptionService _subscriptionService;
        private readonly IStripeValidationService _stripeValidationService;
        private readonly ILoggingService _loggingService;
        private readonly ISignedUrlService _signedUrlService;
        private readonly IConfiguration _configuration;

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
            IConfiguration configuration)
        {
            _context = context;
            _authService = authService;
            _userService = userService;
            _subscriptionService = subscriptionService;
            _stripeValidationService = stripeValidationService;
            _loggingService = loggingService;
            _signedUrlService = signedUrlService;
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
                        ExpertTimezone = s.SearchHire.ExpertTimezone, // ✅ INTERNACIONALIZACIÓN
                        ExpertCountry = s.SearchHire.ExpertCountry, // ✅ INTERNACIONALIZACIÓN
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
                                Price = s.SearchHire.SearchService.Price,
                                ExpertLatitude = s.SearchHire.SearchService.ExpertProfile?.Latitude,
                                ExpertLongitude = s.SearchHire.SearchService.ExpertProfile?.Longitude,
                                LocationRange = s.SearchParameters?.FirstOrDefault()?.LocationRange ?? 50
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
                return StatusCode(500, new { message = ex.Message });
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

                // ✅ COMENTADO: Verificación de teléfono ya no es necesaria
                /*
                if (!user.PhoneVerified)
                {
                    return StatusCode(403, new { message = "Phone verification required to create searches" });
                }
                */

                var service = await _context.SearchServices
                    .Include(ss => ss.ExpertProfile)
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
                    var options = new SessionCreateOptions
                    {
                        PaymentMethodTypes = new List<string> { "card" },
                        LineItems = new List<SessionLineItemOptions>
                        {
                            new SessionLineItemOptions
                            {
                                PriceData = new SessionLineItemPriceDataOptions
                                {
                                    Currency = "eur",
                                    UnitAmount = checked((long)Math.Round(amountToCharge * 100)),
                                    ProductData = new SessionLineItemPriceDataProductDataOptions
                                    {
                                        Name = $"Payment for Service {service.Id}"
                                    }
                                    // ✅ STRIPE TAX (Docs 2026): NO especificar TaxBehavior para que Stripe use el default automático configurado en Dashboard
                                    // Si el Dashboard está en "Automático", Stripe aplicará según moneda: USD/CAD → exclusive, resto → inclusive
                                    // Si se especifica, solo se permiten: "inclusive" o "exclusive" (no "unspecified" ni "automatic")
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
                        Metadata = new Dictionary<string, string>
                        {
                            { "userId", userId.ToString() },
                            { "serviceId", service.Id.ToString() },
                            { "amount", amountToCharge.ToString() },
                            { "pendingHire", "true" },
                            // ✅ OPTIMIZACIÓN: Solo enviar campos esenciales para evitar exceder límite de 500 caracteres
                            // En lugar de serializar DTOs completos, enviar solo IDs y campos críticos
                            { "searchId", "0" }, // ✅ CreateSearchDto no tiene Id (se crea después del pago)
                            { "searchTitle", searchDto?.Title?.Length > 100 ? searchDto.Title.Substring(0, 100) : searchDto?.Title ?? "" },
                            { "searchDescription", searchDto?.Description?.Length > 100 ? searchDto.Description.Substring(0, 100) : searchDto?.Description ?? "" },
                            { "frequency", searchDto?.Frequency.ToString() ?? "24" },
                            { "keywords", parameterDto?.Keywords?.Length > 100 ? parameterDto.Keywords.Substring(0, 100) : parameterDto?.Keywords ?? "" },
                            { "userSearch", parameterDto?.UserSearch?.Length > 200 ? parameterDto.UserSearch.Substring(0, 200) : parameterDto?.UserSearch ?? "" },
                            { "latitude", parameterDto?.Latitude ?? "" },
                            { "longitude", parameterDto?.Longitude ?? "" },
                            { "locationName", parameterDto?.LocationName?.Length > 100 ? parameterDto.LocationName.Substring(0, 100) : parameterDto?.LocationName ?? "" },
                            { "categoryId", parameterDto?.Category?.ToString() ?? "" },
                            { "serviceTypeId", parameterDto?.ServiceTypeId?.ToString() ?? "" },
                            { "locationRange", parameterDto?.LocationRange?.ToString() ?? "" }
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
                    var idempotencyKey = IdempotencyKeyHelper.ForCheckout(
                        userId, service.Id,
                        amountToCharge.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        searchDto?.Title, searchDto?.Description,
                        parameterDto?.Keywords, parameterDto?.UserSearch,
                        parameterDto?.Latitude, parameterDto?.Longitude, parameterDto?.LocationName,
                        parameterDto?.Category?.ToString(), parameterDto?.ServiceTypeId?.ToString(),
                        searchDto?.Frequency.ToString(), parameterDto?.LocationRange?.ToString()); // 🔧 FIX: frequency y locationRange también van en el body → deben discriminar la clave (si no, idempotency_error 400)
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

                    return StatusCode(500, new { message = ex.Message });
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

                    return StatusCode(500, new { message = ex.Message });
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

                return StatusCode(500, new { message = ex.Message });
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

                return StatusCode(500, new { message = ex.Message });
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

                // ✅ COMENTADO: Verificación de teléfono ya no es necesaria
                /*
                if (!user.PhoneVerified)
                {
                    return StatusCode(403, new { message = "Phone verification required to create searches" });
                }
                */

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

                return StatusCode(500, new { message = ex.Message });
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
                            ExpertTimezone = s.SearchHire.ExpertTimezone, // ✅ INTERNACIONALIZACIÓN
                            ExpertCountry = s.SearchHire.ExpertCountry, // ✅ INTERNACIONALIZACIÓN
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

                return StatusCode(500, new { message = ex.Message });
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

                return StatusCode(500, new { message = ex.Message });
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

                return StatusCode(500, new { message = ex.Message });
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
                        ExpertTimezone = search.SearchHire.ExpertTimezone, // ✅ INTERNACIONALIZACIÓN
                        ExpertCountry = search.SearchHire.ExpertCountry, // ✅ INTERNACIONALIZACIÓN
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
                            Price = search.SearchHire.SearchService.Price,
                            // ✅ NUEVOS CAMPOS: Información de ubicación del experto
                            ExpertLatitude = search.SearchHire.SearchService.ExpertProfile?.Latitude,
                            ExpertLongitude = search.SearchHire.SearchService.ExpertProfile?.Longitude,
                            LocationRange = search.SearchParameters?.FirstOrDefault()?.LocationRange ?? 50 // Rango por defecto de 50km
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

                return StatusCode(500, new { message = ex.Message });
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
    }
}