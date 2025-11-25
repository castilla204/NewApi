using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

        public SearchController(
            AppDbContext context,
            IAuthorizationServices authService,
            IUserService userService,
            ISubscriptionService subscriptionService,
            IStripeValidationService stripeValidationService,
            ILoggingService loggingService,
            ISignedUrlService signedUrlService)
        {
            _context = context;
            _authService = authService;
            _userService = userService;
            _subscriptionService = subscriptionService;
            _stripeValidationService = stripeValidationService;
            _loggingService = loggingService;
            _signedUrlService = signedUrlService;
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
                // Default to "pending" (ID = 1)
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

                // Mapear a DTOs
                var searchDtos = searches.Select(s => new SearchListDto
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
                        StatusTranslated = s.SearchHire.Status.StatusValue.ToSpanishTranslation(),
                        CreatedAt = s.SearchHire.CreatedAt,
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

                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    try
                    {
                        // ✅ All payments are now processed through Stripe - no internal balance system
                        var amountToCharge = service.Price;

                        var domain = "https://inspecciono.com";
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
                                    },
                                    Quantity = 1
                                }
                            },
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
                                { "searchData", JsonSerializer.Serialize(searchDto) },
                                { "parameters", JsonSerializer.Serialize(parameterDto) }
                            },
                            // ✅ CAPTURA MANUAL: Autoriza el pago pero no lo captura hasta validar todo en el webhook
                            // Esto evita perder comisiones si algo falla después del pago
                            PaymentIntentData = new SessionPaymentIntentDataOptions
                            {
                                CaptureMethod = "manual"
                            }
                        };

                        var serviceStripe = new SessionService();
                        var session = await serviceStripe.CreateAsync(options);
                        await transaction.CommitAsync();

                        return Ok(new { url = session.Url });
                    }
                        catch (StripeException ex)
                        {
                            await transaction.RollbackAsync();
                            return StatusCode(500, new { message = ex.Message });
                        }
                        catch (Exception ex)
                        {
                            await transaction.RollbackAsync();
                            return StatusCode(500, new { message = ex.Message });
                        }
                    });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPut("{searchId}/revise")]
        public async Task<IActionResult> MarkAsRevised(int searchId)
        {
            try
            {
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

                return Ok(new { message = "Search marked as revised" });
            }
            catch (Exception ex)
            {
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

                return Ok(new { search.Id });
            }
            catch (Exception ex)
            {
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
                            StatusTranslated = s.SearchHire.Status.StatusValue.ToSpanishTranslation(),
                            CreatedAt = s.SearchHire.CreatedAt,
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

                return Ok(response);
            }
            catch (Exception ex)
            {
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

                return Ok(new { isActive = search.IsActive });
            }
            catch (Exception ex)
            {
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

                return Ok(new { message = "Search updated successfully" });
            }
            catch (Exception ex)
            {
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
                        StatusTranslated = search.SearchHire.Status.StatusValue.ToSpanishTranslation(),
                        CreatedAt = search.SearchHire.CreatedAt,
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

                return Ok(searchDto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        /// <summary>
        /// Obtener detalles completos de una búsqueda (optimizado para SearchDetails)
        /// </summary>
        [HttpGet("{searchId}/details-complete")]
        public async Task<IActionResult> GetSearchDetailsComplete(int searchId)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    // En modo desarrollo, usar un usuario por defecto
                    if (Request.Headers.ContainsKey("X-Development-Mode"))
                    {
                        userId = 38; // Usuario por defecto para desarrollo
                    }
                    else
                    {
                        return Unauthorized(new { message = "Invalid user identification" });
                    }
                }

                // Cargar búsqueda con todas las relaciones necesarias
                var search = await _context.Searches
                    .Include(s => s.User)
                    .Include(s => s.SearchParameters) // ✅ NUEVO: Para obtener el rango de ubicación
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Client)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Status)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ExpertProfile) // ✅ NUEVO: Para obtener coordenadas del experto
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                        .ThenInclude(st => st.ServiceTypeCategory)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.SearchService)
                        .ThenInclude(ss => ss.SelectedDeliverableTypes)
                        .ThenInclude(ssdt => ssdt.DeliverableType) // ✅ NUEVO: Para obtener tipos de reportes requeridos
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Appointment)
                        .ThenInclude(a => a.Status)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Appointment)
                        .ThenInclude(a => a.Timers)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Deliverables)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Disputes)
                    .FirstOrDefaultAsync(s => s.Id == searchId &&
                        (s.UserId == userId || 
                         _authService.IsAdmin(User) || 
                         (s.SearchHire != null && (s.SearchHire.ExpertId == userId || 
                          (s.SearchHire.Expert != null && s.SearchHire.Expert.Id == userId)))));

                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                // Obtener configuración de distribución de dinero a través del servicio
                var systemStatusService = HttpContext.RequestServices.GetRequiredService<SystemStatusService>();
                var moneyDistribution = await systemStatusService.GetMoneyDistributionAsync(
                    search.SearchHire.Status.StatusValue, 
                    search.SearchHire.SearchService?.CategoryId, 
                    search.SearchHire.SearchService?.ServiceType?.ServiceTypeCategoryId);

                // Obtener la categoría del servicio
                CategoryDto category = null;
                if (search.SearchHire?.SearchService?.ServiceType?.ServiceTypeCategory != null)
                {
                    category = new CategoryDto
                    {
                        Id = search.SearchHire.SearchService.ServiceType.ServiceTypeCategory.Id,
                        Name = search.SearchHire.SearchService.ServiceType.ServiceTypeCategory.Name,
                        IsActive = search.SearchHire.SearchService.ServiceType.ServiceTypeCategory.IsActive,
                        CreatedAt = search.SearchHire.SearchService.ServiceType.ServiceTypeCategory.CreatedAt,
                        UpdatedAt = search.SearchHire.SearchService.ServiceType.ServiceTypeCategory.UpdatedAt
                    };
                }

                // Obtener la reseña para este SearchHire si existe
                ReviewDto review = null;
                if (search.SearchHire != null)
                {
                    var reviewEntity = await _context.Reviews
                        .Include(r => r.Reviewer)
                        .Include(r => r.ImagesCollection)
                        .FirstOrDefaultAsync(r => r.SearchHireId == search.SearchHire.Id);

                    if (reviewEntity != null)
                    {
                        review = new ReviewDto
                        {
                            Id = reviewEntity.Id,
                            Score = reviewEntity.Score,
                            Description = reviewEntity.Description,
                            CreatedAt = reviewEntity.CreatedAt,
                            Reviewer = new UserDto
                            {
                                Id = reviewEntity.Reviewer.Id,
                                Name = reviewEntity.Reviewer.Name,
                                Email = reviewEntity.Reviewer.Email,
                                ProfilePictureUrl = null
                            },
                            ImageUrls = reviewEntity.ImagesCollection?.Select(img => img.ImageUrl).ToList() ?? new List<string>()
                        };
                    }
                }

                // ✅ DEBUG: Log para verificar datos del experto
                var locationRange = search.SearchParameters?.FirstOrDefault()?.LocationRange;

                // ✅ NUEVO: Cargar disponibilidad del experto si existe
                ExpertProfileDto? expertProfileDto = null;
                if (search.SearchHire?.SearchService?.ExpertProfile != null)
                {
                    var expertProfile = search.SearchHire.SearchService.ExpertProfile;
                    var currentAvailability = await _context.ExpertAvailabilities
                        .Where(ea => ea.ExpertId == expertProfile.Id && ea.IsActive && ea.EffectiveTo == null)
                        .OrderByDescending(ea => ea.EffectiveFrom)
                        .FirstOrDefaultAsync();

                    CurrentExpertAvailabilityDto? availabilityDto = null;
                    if (currentAvailability != null)
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
                          Id = expertProfile.Id,
                          ProfilePictureUrl = ResolveProfilePictureUrl(expertProfile),
                          Description = expertProfile.Description ?? string.Empty,
                          StripeAccountId = expertProfile.StripeAccountId,
                          CreatedAt = expertProfile.CreatedAt,
                          User = search.SearchHire.Expert != null ? new UserDto
                          {
                              Id = search.SearchHire.Expert.Id,
                              Name = search.SearchHire.Expert.Name,
                              Email = search.SearchHire.Expert.Email,
                              ProfilePictureUrl = null
                          } : null,
                        Reviews = new List<ReviewDto>(), // Las reviews se cargan por separado si es necesario
                        Latitude = expertProfile.Latitude ?? string.Empty,
                        Longitude = expertProfile.Longitude ?? string.Empty,
                        StripeStatus = expertProfile.StripeStatus,
                        StripeStatusDetails = expertProfile.StripeStatusDetails,
                        OnboardingCompleted = expertProfile.OnboardingCompleted,
                        IsOnVacation = expertProfile.IsOnVacation,
                        CurrentAvailability = availabilityDto, // ✅ NUEVO: Horarios de disponibilidad
                        // ✅ FUTURE REQUIREMENTS
                        StripeFutureRequirements = expertProfile.StripeFutureRequirements,
                        StripeFutureDueAt = expertProfile.StripeFutureDueAt
                    };
                }

                // Crear respuesta completa con todos los datos usando DTO
                var searchDetailsComplete = new SearchDetailsCompleteResponseDto
                {
                    Search = new SearchListDto
                    {
                        Id = search.Id,
                        UserId = search.UserId,
                        Title = search.Title,
                        Description = search.Description,
                        Frequency = search.Frequency,
                        IsActive = search.IsActive,
                        IsRevised = search.IsRevised,
                        CreatedAt = search.CreatedAt,
                        User = new UserDto
                        {
                            Id = search.User.Id,
                            Name = search.User.Name,
                            Email = search.User.Email,
                            ProfilePictureUrl = null // Los clientes no tienen foto de perfil
                        },
                        SearchHire = search.SearchHire != null ? new SearchHireDto
                        {
                            Id = search.SearchHire.Id,
                            Status = search.SearchHire.Status.StatusValue,
                            CreatedAt = search.SearchHire.CreatedAt,
                            Expert = search.SearchHire.Expert != null ? new UserDto
                            {
                                Id = search.SearchHire.Expert.Id,
                                Name = search.SearchHire.Expert.Name,
                                Email = search.SearchHire.Expert.Email,
                                  ProfilePictureUrl = ResolveProfilePictureUrl(search.SearchHire.SearchService?.ExpertProfile)
                            } : null,
                            Service = search.SearchHire.SearchService != null ? new ServiceInfo
                            {
                                Id = search.SearchHire.SearchService.Id,
                                ServiceTypeId = search.SearchHire.SearchService.ServiceTypeId,
                                ServiceTypeName = search.SearchHire.SearchService.ServiceType?.Name ?? string.Empty,
                                ServiceTypeCategoryId = search.SearchHire.SearchService.ServiceType?.ServiceTypeCategoryId,
                                ServiceTypeCategoryName = search.SearchHire.SearchService.ServiceType?.ServiceTypeCategory?.Name,
                                RequiresAppointment = false,
                                Price = search.SearchHire.SearchService.Price,
                                // ✅ NUEVOS CAMPOS: Información de ubicación del experto
                                ExpertLatitude = search.SearchHire.SearchService.ExpertProfile?.Latitude,
                                ExpertLongitude = search.SearchHire.SearchService.ExpertProfile?.Longitude,
                                LocationRange = search.SearchParameters?.FirstOrDefault()?.LocationRange ?? 50 // Rango por defecto de 50km
                            } : null,
                            // ✅ NUEVO: Información completa del estado
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
                    },
                    MoneyDistribution = moneyDistribution != null ? new MoneyDistributionConfigDto
                    {
                        ClientPercentage = moneyDistribution.ClientPercentage,
                        ExpertPercentage = moneyDistribution.ExpertPercentage,
                        PlatformPercentage = moneyDistribution.PlatformPercentage,
                        Source = "SearchHire",
                        Status = "Active"
                    } : null,
                    Category = category,
                    Review = review,
                    Appointment = search.SearchHire?.Appointment != null ? new AppointmentDto
                    {
                        Id = search.SearchHire.Appointment.Id,
                        SearchHireId = search.SearchHire.Appointment.SearchHireId,
                        Status = search.SearchHire.Appointment.Status?.StatusValue ?? string.Empty,
                        ProposedDate = search.SearchHire.Appointment.ProposedDate,
                        ProposedTime = search.SearchHire.Appointment.ProposedTime,
                        Location = search.SearchHire.Appointment.Location,
                        Latitude = search.SearchHire.Appointment.Latitude,
                        Longitude = search.SearchHire.Appointment.Longitude,
                        DoorNumber = search.SearchHire.Appointment.DoorNumber,
                        OwnerPhone = search.SearchHire.Appointment.OwnerPhone,
                        SiteDetails = search.SearchHire.Appointment.SiteDetails,
                        RejectionCount = search.SearchHire.Appointment.RejectionCount,
                        ClientCancellationCount = search.SearchHire.Appointment.ClientCancellationCount,
                        ExpertCancellationCount = search.SearchHire.Appointment.ExpertCancellationCount,
                        LastRejectionAt = search.SearchHire.Appointment.LastRejectionAt,
                        LastClientCancellationAt = search.SearchHire.Appointment.LastClientCancellationAt,
                        LastExpertCancellationAt = search.SearchHire.Appointment.LastExpertCancellationAt,
                        LastProposalAt = search.SearchHire.Appointment.LastProposalAt,
                        LastResponseAt = search.SearchHire.Appointment.LastResponseAt,
                        CreatedAt = search.SearchHire.Appointment.CreatedAt,
                        UpdatedAt = search.SearchHire.Appointment.UpdatedAt,
                        ClientName = search.SearchHire?.Client?.Name,
                        ExpertName = search.SearchHire?.Expert?.Name,
                        Amount = search.SearchHire?.Amount ?? 0,
                        // ✅ NUEVOS CAMPOS: Información de ubicación del experto
                        ExpertLatitude = search.SearchHire?.SearchService?.ExpertProfile?.Latitude,
                        ExpertLongitude = search.SearchHire?.SearchService?.ExpertProfile?.Longitude,
                        LocationRange = search.SearchParameters?.FirstOrDefault()?.LocationRange ?? 50, // Rango por defecto de 50km
                        // ✅ NUEVO: Información completa del estado
                        StatusInfo = search.SearchHire.Appointment.Status != null ? new SystemStatusDto
                        {
                            Id = search.SearchHire.Appointment.Status.Id,
                            StatusType = search.SearchHire.Appointment.Status.StatusType,
                            StatusName = search.SearchHire.Appointment.Status.StatusName,
                            StatusValue = search.SearchHire.Appointment.Status.StatusValue,
                            DisplayName = search.SearchHire.Appointment.Status.DisplayName,
                            Description = search.SearchHire.Appointment.Status.Description,
                            Color = search.SearchHire.Appointment.Status.Color,
                            IsActive = search.SearchHire.Appointment.Status.IsActive,
                            IsFinalizationStatus = search.SearchHire.Appointment.Status.IsFinalizationStatus,
                            SortOrder = search.SearchHire.Appointment.Status.SortOrder,
                            CreatedAt = search.SearchHire.Appointment.Status.CreatedAt,
                            UpdatedAt = search.SearchHire.Appointment.Status.UpdatedAt
                        } : null,
                        Timers = search.SearchHire.Appointment.Timers?.Select(t => new AppointmentTimerDto
                        {
                            Id = t.Id,
                            AppointmentId = t.AppointmentId,
                            TimerType = t.TimerType,
                            StartTime = t.StartTime,
                            EndTime = t.EndTime,
                            IsExpired = t.IsExpired,
                            ExpiredAt = t.ExpiredAt
                        }).ToList() ?? new List<AppointmentTimerDto>()
                    } : null,
                    Deliverables = search.SearchHire?.Deliverables?.Select(d => new DeliverableDto
                    {
                        Id = d.Id,
                        Type = d.Type,
                        Url = ResolveDeliverableUrl(d),
                        CreatedAt = d.CreatedAt
                    }).ToList() ?? new List<DeliverableDto>(),
                    RequiredDeliverableTypes = search.SearchHire?.SearchService?.SelectedDeliverableTypes?
                        .Where(ssdt => ssdt.IsSelected && ssdt.DeliverableType != null)
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
                        .OrderBy(dt => dt.SortOrder)
                        .ToList() ?? new List<DeliverableTypeDto>(),
                    Disputes = search.SearchHire?.Disputes?.Select(d => new DisputeDto
                    {
                        Id = d.Id,
                        SearchHireId = d.SearchHireId,
                        ReporterId = d.ReporterId,
                        Status = d.Status,
                        Reason = d.Reason,
                        ExpertResponse = d.ExpertResponse,
                        ExpertResponseDeadline = d.ExpertResponseDeadline,
                        ExpertResponseAt = d.ExpertResponseAt,
                        CanExpertRespond = d.CanExpertRespond,
                        CreatedAt = d.CreatedAt
                    }).ToList() ?? new List<DisputeDto>(),
                    ExpertProfile = expertProfileDto // ✅ NUEVO: Perfil completo del experto con horarios
                };

                return Ok(searchDetailsComplete);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error" });
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

    public class CreateSearchWithHireDto
    {
        public CreateSearchDto SearchDto { get; set; }
        public CreateSearchParameterDto ParameterDto { get; set; }
    }
}