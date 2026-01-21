using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Google.Cloud.Storage.V1;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;

namespace newApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ReviewController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly StorageClient _storageClient;
        private readonly ILoggingService _loggingService;
        private readonly ISignedUrlService _signedUrlService;

        public ReviewController(
            AppDbContext context,
            IConfiguration configuration,
            StorageClient storageClient,
            ILoggingService loggingService,
            ISignedUrlService signedUrlService)
        {
            _context = context;
            _configuration = configuration;
            _storageClient = storageClient;
            _loggingService = loggingService;
            _signedUrlService = signedUrlService;
        }

        [HttpPost("search-hire/{searchHireId}")]
        public async Task<IActionResult> CreateReview(int searchHireId, [FromForm] CreateReviewDto reviewDto)
        {
            try
            {
                // Obtener el usuario autenticado
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Verificar que el searchHireId exista y que el usuario sea el cliente
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.SearchService)
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId && sh.ClientId.HasValue && sh.ClientId.Value == userId);
                if (searchHire == null)
                {
                    return NotFound(new { message = "SearchHire not found or you are not authorized to review this hire" });
                }

                // Verificar que ExpertId no sea nulo (redundante pero para seguridad)
                if (searchHire.ExpertId == null)
                {
                    return BadRequest(new { message = "Cannot create review: No expert associated with this service" });
                }

                // Verificar el estado del SearchHire
                if (searchHire.Status.StatusValue != SearchHireStatus.DisputeResolvedClient.ToStringValue() &&
                    searchHire.Status.StatusValue != SearchHireStatus.DisputeResolvedExpert.ToStringValue() &&
                    searchHire.Status.StatusValue != SearchHireStatus.Completed.ToStringValue())
                {
                    return BadRequest(new { message = "Reviews can only be submitted for SearchHires in 'dispute-resolved-client', 'dispute-resolved-expert' or 'completed' status" });
                }

                // Verificar si ya existe una reseña para este SearchHire
                var existingReview = await _context.Reviews
                    .FirstOrDefaultAsync(r => r.ReviewerId == userId && r.ExpertId == searchHire.ExpertId && r.SearchHireId == searchHireId);
                if (existingReview != null)
                {
                    return BadRequest(new { message = "A review for this SearchHire has already been submitted" });
                }

                // Validar la puntuación
                if (reviewDto.Score < 1 || reviewDto.Score > 5)
                {
                    return BadRequest(new { message = "Score must be between 1 and 5" });
                }

                // Crear la reseña
                var review = new Review
                {
                    ReviewerId = userId,
                    ExpertId = searchHire.ExpertId.Value, // Safe due to AIId and ExpertId checks
                    SearchHireId = searchHireId,
                    Score = reviewDto.Score,
                    Description = reviewDto.Description,
                    CreatedAt = DateTime.UtcNow,
                    Images = new string[0] // Inicializar como array vacío, ya que usamos ImagesCollection
                };

                _context.Reviews.Add(review);
                await _context.SaveChangesAsync(); // Guardar para obtener el review.Id

                // Procesar y subir las imágenes
                var imageUrls = new List<string>();
                if (reviewDto.Images != null && reviewDto.Images.Any())
                {
                    var bucketName = _configuration["GoogleCloud:BucketName"];
                    foreach (var imageFile in reviewDto.Images)
                    {
                        var extension = Path.GetExtension(imageFile.FileName).ToLowerInvariant();
                        if (!new[] { ".jpg", ".jpeg", ".png" }.Contains(extension))
                        {
                            return BadRequest(new { message = "Only JPG and PNG images are allowed" });
                        }

                        var uniqueFileName = $"{Guid.NewGuid()}{extension}";
                        var objectName = $"reviews/{uniqueFileName}";

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
                                    bucket: bucketName,
                                    objectName: objectName,
                                    contentType: "image/jpeg",
                                    source: outputStream
                                    // ✅ REMOVIDO: PredefinedAcl no es compatible con uniform bucket-level access
                                    // options: new UploadObjectOptions { PredefinedAcl = PredefinedObjectAcl.Private }
                                );
                            }
                        }

                        var imageUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";

                        var reviewImage = new ReviewImage
                        {
                            ReviewId = review.Id,
                            ImageUrl = imageUrl,
                            ImageObjectName = objectName,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.ReviewImages.Add(reviewImage);
                        imageUrls.Add(ResolveReviewImageUrl(reviewImage));
                    }
                    await _context.SaveChangesAsync();
                }

                // Recargar review con SearchHire para obtener ExpertCountry
                var reviewWithSearchHire = await _context.Reviews
                    .Include(r => r.SearchHire)
                    .FirstOrDefaultAsync(r => r.Id == review.Id);

                // Preparar la respuesta
                var response = new ReviewResponseDto
                {
                    Id = review.Id,
                    ReviewerId = review.ReviewerId,
                    ExpertId = review.ExpertId,
                    SearchHireId = review.SearchHireId,
                    Score = review.Score,
                    Description = review.Description,
                    ImageUrls = imageUrls,
                    CreatedAt = review.CreatedAt,
                    // ✅ INTERNACIONALIZACIÓN: País donde se realizó la contratación
                    Country = reviewWithSearchHire?.SearchHire?.ExpertCountry
                };

                return Ok(new { message = "Review created successfully", review = response });
            }
            catch (Exception ex)
            {
                // 🚨 LOG CRÍTICO: Guardar en tabla Logs
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Review creation failed",
                    details: $"Failed to create review for SearchHire {searchHireId}. Error: {ex.Message}. StackTrace: {ex.StackTrace}",
                    userId: int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int userId) ? userId : null,
                    source: "ReviewController.CreateReview",
                    relatedEntityType: "Review",
                    relatedEntityId: searchHireId,
                    additionalData: new { 
                        SearchHireId = searchHireId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        ReviewDto = new { 
                            Score = reviewDto?.Score,
                            Description = reviewDto?.Description,
                            ImageCount = reviewDto?.Images?.Length ?? 0
                        }
                    }
                );
                
                // Determinar el tipo de error y devolver mensaje específico
                var errorMessage = ex switch
                {
                    ArgumentException => "Invalid data provided for the review",
                    UnauthorizedAccessException => "You are not authorized to create this review",
                    InvalidOperationException => "Review cannot be created in the current state",
                    _ => "An unexpected error occurred while creating the review"
                };
                
                return StatusCode(500, new { 
                    message = errorMessage,
                    details = ex.Message,
                    searchHireId = searchHireId
                });
            }
        }

        [HttpGet("expert/{expertId}")]
        public async Task<IActionResult> GetExpertReviews(int expertId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            try
            {
                // Verificar que el experto exista
                var expertExists = await _context.Users.AnyAsync(u => u.Id == expertId && u.Role == UserRole.Expert);
                if (!expertExists)
                {
                    return NotFound(new { message = "Expert not found" });
                }

                // Validar parámetros
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 20;

                var query = _context.Reviews
                    .Where(r => r.ExpertId == expertId)
                    .Include(r => r.Reviewer)
                    .Include(r => r.ImagesCollection)
                    .Include(r => r.SearchHire); // ✅ INTERNACIONALIZACIÓN: Cargar SearchHire para obtener ExpertCountry

                var totalCount = await query.CountAsync();

                // Obtener reseñas paginadas para el experto especificado
                var reviewEntities = await query
                    .OrderByDescending(r => r.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                var reviews = reviewEntities
                    .Select(r => new ReviewResponseDto
                    {
                        Id = r.Id,
                        ReviewerId = r.ReviewerId,
                        ExpertId = r.ExpertId,
                        SearchHireId = r.SearchHireId,
                        Score = r.Score,
                        Description = r.Description,
                        ImageUrls = r.ImagesCollection?
                            .Select(ri => ResolveReviewImageUrl(ri))
                            .Where(url => !string.IsNullOrEmpty(url))
                            .ToList() ?? new List<string>(),
                        CreatedAt = r.CreatedAt,
                        // ✅ INTERNACIONALIZACIÓN: País donde se realizó la contratación
                        Country = r.SearchHire?.ExpertCountry
                    })
                    .ToList();

                return Ok(new
                {
                    reviews,
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
                return StatusCode(500, new { message = "An error occurred while retrieving reviews" });
            }
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
    }
}