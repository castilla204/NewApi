using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using newApi.DataLayer;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;
using Google.Cloud.Storage.V1;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System.IO;
using newApi.DataLayer.Models.PostGresModels.newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SearchServiceController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SearchServiceController> _logger;
        private readonly StorageClient _storageClient;

        public SearchServiceController(
            AppDbContext context,
            IConfiguration configuration,
            ILogger<SearchServiceController> logger,
            StorageClient storageClient)
        {
            _context = context;
            _configuration = configuration;
            _logger = logger;
            _storageClient = storageClient;
        }

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> CreateSearchService([FromForm] CreateSearchServiceRequestDto request)
        {
            try
            {
                // Obtener el ID del usuario autenticado
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Verificar que el ExpertProfile pertenece al usuario
                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.Id == request.ExpertProfileId && ep.UserId == userId);
                if (expertProfile == null)
                {
                    return BadRequest(new { message = "Expert profile not found or does not belong to the user" });
                }

                // Validar CategoryId
                var category = await _context.Categories.FindAsync(request.CategoryId);
                if (category == null)
                {
                    return BadRequest(new { message = "Category not found" });
                }

                // Validar DurationInHours
                if (request.DurationInHours <= 0)
                {
                    return BadRequest(new { message = "Duration must be greater than 0 hours" });
                }

                // Validar imágenes
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png" };
                if (request.Images != null && request.Images.Any())
                {
                    foreach (var image in request.Images)
                    {
                        var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
                        if (!allowedExtensions.Contains(extension))
                        {
                            return BadRequest(new { message = $"Invalid image format for {image.FileName}. Only JPG and PNG are allowed" });
                        }
                        if (image.Length > 5 * 1024 * 1024) // Límite de 5MB
                        {
                            return BadRequest(new { message = $"Image {image.FileName} exceeds 5MB limit" });
                        }
                    }
                }

                // Crear el SearchService
                var searchService = new SearchService
                {
                    ExpertProfileId = request.ExpertProfileId,
                    CategoryId = request.CategoryId,
                    Price = request.Price,
                    Conditions = request.Conditions,
                    DurationInHours = request.DurationInHours,
                    CreatedAt = DateTime.UtcNow
                };

                _context.SearchServices.Add(searchService);
                await _context.SaveChangesAsync(); // Guardar para obtener el ID

                // Subir imágenes a Google Cloud Storage
                var bucketName = _configuration["GoogleCloud:BucketName"];
                if (string.IsNullOrEmpty(bucketName))
                {
                    _logger.LogError("Google Cloud bucket name not found in configuration");
                    return StatusCode(500, new { message = "Google Cloud bucket name not configured" });
                }

                var imageUrls = new List<string>();
                if (request.Images != null && request.Images.Any())
                {
                    foreach (var imageFile in request.Images)
                    {
                        var extension = Path.GetExtension(imageFile.FileName).ToLowerInvariant();
                        var uniqueFileName = $"{Guid.NewGuid()}{extension}";
                        var objectName = $"services/{uniqueFileName}";

                        // Redimensionar la imagen
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
                                    bucket: bucketName,
                                    objectName: objectName,
                                    contentType: "image/jpeg",
                                    source: outputStream
                                );
                            }
                        }

                        // Generar la URL pública
                        var imageUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";
                        imageUrls.Add(imageUrl);

                        // Crear SearchServiceImage
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

                return Ok(new
                {
                    message = "Search service created successfully",
                    searchService = new
                    {
                        searchService.Id,
                        searchService.ExpertProfileId,
                        searchService.CategoryId,
                        searchService.Price,
                        searchService.Conditions,
                        searchService.DurationInHours,
                        searchService.CreatedAt,
                        ImageUrls = imageUrls
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating search service");
                return StatusCode(500, new { message = "Failed to create search service" });
            }
        }



        [Authorize]
        [HttpGet]
        public async Task<IActionResult> GetExpertServices()
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Get the expert profile first
                var expertProfile = await _context.ExpertProfiles
                    .FirstOrDefaultAsync(ep => ep.UserId == userId);

                if (expertProfile == null)
                {
                    return NotFound(new { message = "Expert profile not found" });
                }

                // Get all services for this expert including images
                var services = await _context.SearchServices
                    .Include(ss => ss.Images)
                    .Where(ss => ss.ExpertProfileId == expertProfile.Id)
                    .Select(ss => new SearchServiceDto
                    {
                        Id = ss.Id,
                        CategoryId = ss.CategoryId,
                        Price = ss.Price,
                        Conditions = ss.Conditions,
                        DurationInHours = ss.DurationInHours,
                        CreatedAt = ss.CreatedAt,
                        ImageUrls = ss.Images.Select(i => i.ImageUrl).ToList()
                    })
                    .OrderByDescending(ss => ss.CreatedAt)
                    .ToListAsync();

                return Ok(services);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving expert services");
                return StatusCode(500, new { message = "Failed to retrieve expert services" });
            }
        }





    }



}