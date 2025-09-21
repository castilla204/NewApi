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
                        StatusTranslated = s.SearchHire.Status.ToSpanishTranslation(), // ✅ NUEVO: Estado traducido al español
                        CreatedAt = s.SearchHire.CreatedAt, // ✅ NUEVO: Fecha de contratación del servicio
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
                _ => isDescending ? query.OrderByDescending(s => s.CreatedAt) : query.OrderBy(s => s.CreatedAt) // Default: CreatedAt
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


                //PARA MANEJAR SUSCRIPCIONES
                //if (activeSearchCount >= subscriptionLimits.MaxSearches)
                //{
                //    return StatusCode(403, new { message = $"You've reached your plan's limit of {subscriptionLimits.MaxSearches} active searches" });
                //}
                //if (searchDto.Frequency < subscriptionLimits.MinSearchInterval)
                //{
                //    return StatusCode(403, new { message = $"Minimum search interval for your plan is {subscriptionLimits.MinSearchInterval} hours" });
                //}

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
                        await _context.SaveChangesAsync(); // Save Search to generate Search.Id

                        var searchParameter = new SearchParameter
                        {
                            Keywords = parameterDto.Keywords,
                            UserSearch = parameterDto.UserSearch,
                            Latitude = parameterDto.Latitude,
                            Longitude = parameterDto.Longitude,
                            LocationName = parameterDto.LocationName, // ✅ NUEVO: Incluir LocationName
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
                        await _context.SaveChangesAsync(); // Save SearchParameter to generate SearchParameterId

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

                        // Validar que el experto no se contrate a sí mismo
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
                        // Calculate the amount to charge after deducting user balance
                        var amountToCharge = Math.Max(0, service.Price - user.Balance);
                        
                        // If amount to charge is 0, user has enough balance to cover the service
                        if (amountToCharge == 0)
                        {
                            // Process the hire directly without payment link since balance covers it
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
                                LocationName = parameterDto.LocationName, // ✅ NUEVO: Incluir LocationName
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

                            // Validar que el experto no se contrate a sí mismo
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

                            return Ok(new { message = "Search created and service hired successfully using balance", searchId = search.Id, searchHireId = searchHire.Id });
                        }

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
                                            Name = $"Payment for Service {service.Id} (after balance deduction)"
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
                                { "amount", service.Price.ToString() },
                                { "chargedAmount", amountToCharge.ToString() },
                                { "balanceUsed", Math.Min(user.Balance, service.Price).ToString() },
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

                // Uncomment to active search limits by subscription
                // var subscriptionLimits = await _subscriptionService.GetUserSubscriptionLimits(userId);
                // if (activeSearchCount >= subscriptionLimits.MaxSearches)
                // {
                //     return StatusCode(403, new { message = $"You've reached your plan's limit of {subscriptionLimits.MaxSearches} active searches" });
                // }
                // if (searchDto.Frequency < subscriptionLimits.MinSearchInterval)
                // {
                //     return StatusCode(403, new { message = $"Minimum search interval for your plan is {subscriptionLimits.MinSearchInterval} hours" });
                // }

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

                // Validar parámetros
                if (request.Page < 1) request.Page = 1;
                if (request.PageSize < 1 || request.PageSize > 50) request.PageSize = 20;

                // Construir query base con includes
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
                            StatusTranslated = s.SearchHire.Status.ToSpanishTranslation(), // ✅ NUEVO: Estado traducido al español
                            CreatedAt = s.SearchHire.CreatedAt,
                            Expert = s.SearchHire.Expert != null ? new UserDto
                            {
                                Name = s.SearchHire.Expert.Name,
                                ProfilePictureUrl = s.SearchHire.Expert.ExpertProfile?.ProfilePictureUrl ?? "/default-avatar.png"
                            } : null
                        } : null
                    };
                }).ToList();

                // ✅ NUEVO: Calcular indicadores de notificaciones para cada búsqueda
                foreach (var searchDto in searchDtos)
                {
                    var search = searches.First(s => s.Id == searchDto.Id);
                    
                    if (search.SearchHire != null)
                    {
                        // Contar mensajes sin leer
                        if (search.SearchHire.Conversations != null && search.SearchHire.Conversations.Any())
                        {
                            searchDto.UnreadMessagesCount = search.SearchHire.Conversations
                                .SelectMany(c => c.Messages)
                                .Count(m => m.SenderId != userId && !m.IsRead);
                        }

                        // Verificar si hay cita pendiente
                        if (search.SearchHire.Appointment != null)
                        {
                            var pendingStatuses = new[] { 
                                "awaiting_appointment", 
                                "appointment_proposed", 
                                "appointment_confirmed" 
                            };
                            
                            if (pendingStatuses.Contains(search.SearchHire.Appointment.Status))
                            {
                                searchDto.HasPendingAppointment = true;
                                searchDto.PendingAppointmentStatus = search.SearchHire.Appointment.Status;
                            }
                        }
                    }
                }

                // ✅ NUEVO: Calcular estadísticas del usuario
                var userStats = await CalculateUserSearchStats(userId);

                // Crear respuesta paginada
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

        /// <summary>
        /// Calcula estadísticas de búsquedas del usuario
        /// </summary>
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

            // Calcular mensajes sin leer
            var unreadMessages = await userSearches
                .Where(s => s.SearchHire != null)
                .SelectMany(s => s.SearchHire.Conversations)
                .SelectMany(c => c.Messages)
                .CountAsync(m => m.SenderId != userId && !m.IsRead);

            // Calcular citas pendientes
            var pendingAppointments = await userSearches
                .Where(s => s.SearchHire != null && s.SearchHire.Appointment != null)
                .CountAsync(s => new[] { 
                    "awaiting_appointment", 
                    "appointment_proposed", 
                    "appointment_confirmed" 
                }.Contains(s.SearchHire.Appointment.Status));

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

                // Verificar si SearchHire existe y su estado permite el cambio
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

        [HttpDelete("{searchId}")]
        public async Task<IActionResult> DeleteSearch(int searchId)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                var search = await _context.Searches.FirstOrDefaultAsync(s => s.Id == searchId);
                if (search == null)
                {
                    return NotFound(new { message = "Search not found" });
                }

                _context.Searches.Remove(search);
                await _context.SaveChangesAsync();

                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting search");
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
                    .Include(s => s.User) // Include User to prevent null reference
                    .Include(s => s.SearchParameters)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Expert)
                        .ThenInclude(e => e.ExpertProfile)
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                        .ThenInclude(st => st.ServiceTypeCategory) // ✅ NUEVO: Incluir ServiceType y ServiceTypeCategory
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Conversations)
                        .ThenInclude(c => c.Messages) // ✅ NUEVO: Incluir mensajes para contar no leídos
                    .Include(s => s.SearchHire)
                        .ThenInclude(sh => sh.Appointment) // ✅ NUEVO: Incluir citas
                    .FirstOrDefaultAsync(s => s.Id == searchId &&
                        (s.UserId == userId || // User is the search owner
                         _authService.IsAdmin(User) || // User is an admin
                         (s.SearchHire != null && s.SearchHire.ExpertId == userId))); // User is the assigned expert

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
                    LocationName = search.SearchParameters.FirstOrDefault()?.LocationName, // ✅ NUEVO: Nombre de la ubicación desde SearchParameters
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
                        StatusTranslated = search.SearchHire.Status.ToSpanishTranslation(), // ✅ NUEVO: Estado traducido al español
                        CreatedAt = search.SearchHire.CreatedAt, // ✅ NUEVO: Fecha de contratación del servicio
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
                            Price = search.SearchHire.SearchService.Price
                        } : null
                    } : null
                };

                // ✅ NUEVO: Calcular indicadores de notificaciones
                if (search.SearchHire != null)
                {
                    // Contar mensajes sin leer
                    if (search.SearchHire.Conversations != null && search.SearchHire.Conversations.Any())
                    {
                        searchDto.UnreadMessagesCount = search.SearchHire.Conversations
                            .SelectMany(c => c.Messages)
                            .Count(m => m.SenderId != userId && !m.IsRead);
                    }

                    // Verificar si hay cita pendiente
                    if (search.SearchHire.Appointment != null)
                    {
                        var pendingStatuses = new[] { 
                            "awaiting_appointment", 
                            "appointment_proposed", 
                            "appointment_confirmed" 
                        };
                        
                        if (pendingStatuses.Contains(search.SearchHire.Appointment.Status))
                        {
                            searchDto.HasPendingAppointment = true;
                            searchDto.PendingAppointmentStatus = search.SearchHire.Appointment.Status;
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
    }
    public class CreateSearchWithHireDto
    {
        public CreateSearchDto SearchDto { get; set; }
        public CreateSearchParameterDto ParameterDto { get; set; }
    }
}