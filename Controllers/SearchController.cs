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
        private readonly ILogger<SearchController> _logger;
        private readonly IAuthorizationServices _authService;
        private readonly IUserService _userService;
        private readonly ISubscriptionService _subscriptionService;

        public SearchController(
            AppDbContext context,
            ILogger<SearchController> logger,
            IAuthorizationServices authService,
            IUserService userService,
            ISubscriptionService subscriptionService)
        {
            _context = context;
            _logger = logger;
            _authService = authService;
            _userService = userService;
            _subscriptionService = subscriptionService;
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
                    query = query.Where(s => s.SearchHire != null && s.SearchHire.Status == request.SearchHireStatus);
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
                        Status = s.SearchHire.Status,
                        StatusTranslated = s.SearchHire.Status.ToSpanishTranslation(),
                        CreatedAt = s.SearchHire.CreatedAt,
                        Expert = s.SearchHire.Expert != null ? new UserDto
                        {
                            Name = s.SearchHire.Expert.Name,
                            ProfilePictureUrl = s.SearchHire.Expert.ExpertProfile?.ProfilePictureUrl ?? "/default-avatar.png"
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
                _logger.LogError(ex, "Error retrieving all searches");
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

                if (!user.PhoneVerified)
                {
                    return StatusCode(403, new { message = "Phone verification required to create searches" });
                }

                var service = await _context.SearchServices.FindAsync(searchDto.ServiceId);
                if (service == null)
                {
                    return NotFound(new { message = "Service not found" });
                }

                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    try
                    {
                        if (user.Balance >= service.Price)
                        {
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

                            var searchParameter = new SearchParameter
                            {
                                Keywords = parameterDto.Keywords,
                                UserSearch = parameterDto.UserSearch,
                                Latitude = parameterDto.Latitude,
                                Longitude = parameterDto.Longitude,
                                LocationName = parameterDto.LocationName,
                                ShippingAvailable = parameterDto.ShippingAvailable,
                                StrictMatchOnly = parameterDto.StrictMatchOnly,
                                Category = parameterDto.Category,
                                LocationRange = parameterDto.LocationRange,
                                MinPrice = parameterDto.MinPrice,
                                MaxPrice = parameterDto.MaxPrice,
                                BrandId = parameterDto.BrandId,
                                ModelId = parameterDto.ModelId,
                                ServiceTypeId = parameterDto.ServiceTypeId,
                                SearchId = search.Id
                            };
                            await _context.SearchParameters.AddAsync(searchParameter);
                            await _context.SaveChangesAsync();

                            if (parameterDto.PlatformIds != null && parameterDto.PlatformIds.Any())
                            {
                                var platforms = await _context.Platforms
                                    .Where(p => parameterDto.PlatformIds.Contains(p.Id))
                                    .ToListAsync();
                                if (platforms.Count != parameterDto.PlatformIds.Count)
                                {
                                    return BadRequest(new { message = "Some platform IDs are invalid" });
                                }
                                foreach (var platform in platforms)
                                {
                                    _context.SearchParameterPlatforms.Add(new SearchParameterPlatform
                                    {
                                        SearchParameterId = searchParameter.SearchParameterId,
                                        PlatformId = platform.Id
                                    });
                                }
                            }

                            var expertProfile = await _context.ExpertProfiles
                            .FirstOrDefaultAsync(z => z.Id == service.ExpertProfileId);

                            var expertuserid = expertProfile?.UserId ?? 0;

                            if (expertuserid == userId)
                            {
                                return BadRequest(new { message = "No puedes contratarte a ti mismo como experto" });
                            }

                            var searchHire = new SearchHire
                            {
                                ClientId = userId,
                                ExpertId = expertuserid,
                                SearchServiceId = service.Id,
                                SearchId = search.Id,
                                Status = SearchHireStatus.Pending.ToStringValue(),
                                Amount = service.Price,
                                CreatedAt = DateTime.UtcNow,
                                CompletionDeadline = DateTime.UtcNow.AddDays(7)
                            };
                            _context.SearchHires.Add(searchHire);

                            user.Balance -= service.Price;
                            _context.FinancialTransactions.Add(new FinancialTransaction
                            {
                                UserId = userId,
                                Amount = -service.Price,
                                TransactionType = "ServicePayment",
                                RelatedEntityType = "SearchHire",
                                RelatedEntityId = searchHire.Id,
                                CreatedAt = DateTime.UtcNow
                            });

                            await _context.SaveChangesAsync();
                            await transaction.CommitAsync();

                            return Ok(new { message = "Search created and service hired successfully", searchId = search.Id, searchHireId = searchHire.Id });
                        }
                        else
                        {
                            var amountToCharge = service.Price;

                            var domain = "https://atrapo.io";
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
                                            UnitAmount = (long)(amountToCharge * 100),
                                            ProductData = new SessionLineItemPriceDataProductDataOptions
                                            {
                                                Name = $"Payment for Service {service.Id}"
                                            }
                                        },
                                        Quantity = 1
                                    }
                                },
                                Mode = "payment",
                                SuccessUrl = $"{domain}/success?userId={userId}&serviceId={service.Id}",
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
                                }
                            };

                            var serviceStripe = new SessionService();
                            var session = await serviceStripe.CreateAsync(options);
                            await transaction.CommitAsync();

                            return Ok(new { url = session.Url });
                        }
                    }
                        catch (StripeException ex)
                        {
                            await transaction.RollbackAsync();
                            _logger.LogError(ex, "Stripe error creating checkout session for userId={UserId}, serviceId={ServiceId}", userId, searchDto.ServiceId);
                            return StatusCode(500, new { message = ex.Message });
                        }
                        catch (Exception ex)
                        {
                            await transaction.RollbackAsync();
                            _logger.LogError(ex, "Error creating search with hire for userId={UserId}", userId);
                            return StatusCode(500, new { message = ex.Message });
                        }
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in CreateSearchWithHire for userId={UserId}");
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
                _logger.LogError(ex, "Error marking search as revised");
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

                if (!user.PhoneVerified)
                {
                    return StatusCode(403, new { message = "Phone verification required to create searches" });
                }

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
                _logger.LogError(ex, "Error creating search");
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
                    query = query.Where(s => s.SearchHire != null && s.SearchHire.Status == request.SearchHireStatus);
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
                        _logger.LogError("Search {SearchId} has no associated User", s.Id);
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
                            Status = s.SearchHire.Status,
                            StatusTranslated = s.SearchHire.Status.ToSpanishTranslation(),
                            CreatedAt = s.SearchHire.CreatedAt,
                            Expert = s.SearchHire.Expert != null ? new UserDto
                            {
                                Name = s.SearchHire.Expert.Name,
                                ProfilePictureUrl = s.SearchHire.Expert.ExpertProfile?.ProfilePictureUrl ?? "/default-avatar.png"
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
                _logger.LogError(ex, "Error retrieving user searches");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        private async Task<UserSearchStats> CalculateUserSearchStats(int userId)
        {
            var userSearches = _context.Searches
                .Where(s => s.UserId == userId)
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
                    .FirstOrDefaultAsync(s => s.Id == searchId && (s.UserId == userId || _authService.IsAdmin(User)));

                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                if (search.SearchHire != null && !new[] { "pending", "awaiting_client_decision" }.Contains(search.SearchHire.Status))
                {
                    return BadRequest(new { message = "No se puede modificar el estado de una búsqueda finalizada" });
                }

                search.IsActive = !search.IsActive;
                await _context.SaveChangesAsync();

                return Ok(new { isActive = search.IsActive });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling search active status");
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
                _logger.LogError(ex, "Error updating search");
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
                    _logger.LogError("Search {SearchId} has no associated User", searchId);
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
                        Status = search.SearchHire.Status,
                        StatusTranslated = search.SearchHire.Status.ToSpanishTranslation(),
                        CreatedAt = search.SearchHire.CreatedAt,
                        Expert = search.SearchHire.Expert != null ? new UserDto
                        {
                            Name = search.SearchHire.Expert.Name,
                            ProfilePictureUrl = search.SearchHire.Expert.ExpertProfile?.ProfilePictureUrl ?? "/default-avatar.png"
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
                _logger.LogError(ex, "Error retrieving search");
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
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Cargar búsqueda con todas las relaciones necesarias
                var search = await _context.Searches
                    .Include(s => s.User)
                    .Include(s => s.SearchParameters) // ✅ NUEVO: Para obtener el rango de ubicación
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Client)
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
                    search.SearchHire.Status, 
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
                _logger.LogInformation("DEBUG - ExpertProfile: {ExpertProfile}, Latitude: {Latitude}, Longitude: {Longitude}, LocationRange: {LocationRange}",
                    search.SearchHire?.SearchService?.ExpertProfile != null ? "EXISTS" : "NULL",
                    search.SearchHire?.SearchService?.ExpertProfile?.Latitude ?? "NULL",
                    search.SearchHire?.SearchService?.ExpertProfile?.Longitude ?? "NULL",
                    locationRange?.ToString() ?? "NULL");

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
                            Email = search.User.Email
                        },
                        SearchHire = search.SearchHire != null ? new SearchHireDto
                        {
                            Id = search.SearchHire.Id,
                            Status = search.SearchHire.Status,
                            CreatedAt = search.SearchHire.CreatedAt,
                            Expert = search.SearchHire.Expert != null ? new UserDto
                            {
                                Id = search.SearchHire.Expert.Id,
                                Name = search.SearchHire.Expert.Name,
                                Email = search.SearchHire.Expert.Email
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
                        DisputeReason = search.SearchHire.Appointment.DisputeReason,
                        CompletedAt = search.SearchHire.Appointment.CompletedAt,
                        CompletedBy = search.SearchHire.Appointment.CompletedBy,
                        RejectionCount = search.SearchHire.Appointment.RejectionCount,
                        CancellationCount = search.SearchHire.Appointment.CancellationCount,
                        LastRejectionAt = search.SearchHire.Appointment.LastRejectionAt,
                        LastProposalAt = search.SearchHire.Appointment.LastProposalAt,
                        LastResponseAt = search.SearchHire.Appointment.LastResponseAt,
                        IsLocked = search.SearchHire.Appointment.IsLocked,
                        CreatedAt = search.SearchHire.Appointment.CreatedAt,
                        UpdatedAt = search.SearchHire.Appointment.UpdatedAt,
                        ClientName = search.SearchHire?.Client?.Name,
                        ExpertName = search.SearchHire?.Expert?.Name,
                        Amount = search.SearchHire?.Amount ?? 0,
                        // ✅ NUEVOS CAMPOS: Información de ubicación del experto
                        ExpertLatitude = search.SearchHire?.SearchService?.ExpertProfile?.Latitude,
                        ExpertLongitude = search.SearchHire?.SearchService?.ExpertProfile?.Longitude,
                        LocationRange = search.SearchParameters?.FirstOrDefault()?.LocationRange ?? 50, // Rango por defecto de 50km
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
                        Url = d.Url,
                        CreatedAt = d.CreatedAt
                    }).ToList() ?? new List<DeliverableDto>(),
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
                    }).ToList() ?? new List<DisputeDto>()
                };

                return Ok(searchDetailsComplete);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving complete search details for SearchId: {SearchId}", searchId);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }


    }

    public class CreateSearchWithHireDto
    {
        public CreateSearchDto SearchDto { get; set; }
        public CreateSearchParameterDto ParameterDto { get; set; }
    }
}