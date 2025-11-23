using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.Services;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using System.Security.Claims;
using newApi.DataLayer.Models;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SearchServiceController : ControllerBase
    {
        private readonly SearchServiceService _searchServiceService;
        private readonly AppDbContext _context;

        public SearchServiceController(
            SearchServiceService searchServiceService,

            AppDbContext context)
        {
            _searchServiceService = searchServiceService;
            _context = context;
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

                var services = await _searchServiceService.GetAllServices(categoryId, serviceTypeId, latitude, longitude, locationRange);
                return Ok(services);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to retrieve services", detail = ex.Message });
            }
        }

        /// <summary>
        /// Obtiene expertos para mostrar en el mapa con información básica (sin filtros de ubicación)
        /// </summary>
        [HttpGet("map-experts")]
        public async Task<IActionResult> GetMapExperts(
            [FromQuery] int categoryId,
            [FromQuery] int serviceTypeId)
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

                var experts = await _searchServiceService.GetMapExperts(categoryId, serviceTypeId);
                return Ok(experts);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to retrieve map experts", detail = ex.Message });
            }
        }

        [HttpGet("expert/{expertId}")]
        public async Task<IActionResult> GetExpertServices(int expertId, [FromQuery] int? serviceTypeId)
        {
            try
            {
                var services = await _searchServiceService.GetExpertServices(expertId, serviceTypeId);
                return Ok(services);
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
    }
}