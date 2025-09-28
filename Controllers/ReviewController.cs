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
        private readonly ILogger<ReviewController> _logger;

        public ReviewController(
            AppDbContext context,
            IConfiguration configuration,
            StorageClient storageClient,
            ILogger<ReviewController> logger)
        {
            _context = context;
            _configuration = configuration;
            _storageClient = storageClient;
            _logger = logger;
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
                    .Include(sh => sh.SearchService)
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId && sh.ClientId == userId);
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
                if (searchHire.Status != SearchHireStatus.DisputeResolvedClient.ToStringValue() &&
                    searchHire.Status != SearchHireStatus.DisputeResolvedExpert.ToStringValue() &&
                    searchHire.Status != SearchHireStatus.Completed.ToStringValue())
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
                                await _storageClient.UploadObjectAsync(
                                    bucket: bucketName,
                                    objectName: objectName,
                                    contentType: "image/jpeg",
                                    source: outputStream
                                );
                            }
                        }

                        var imageUrl = $"https://storage.googleapis.com/{bucketName}/{objectName}";
                        imageUrls.Add(imageUrl);

                        var reviewImage = new ReviewImage
                        {
                            ReviewId = review.Id,
                            ImageUrl = imageUrl,
                            ImageObjectName = objectName,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.ReviewImages.Add(reviewImage);
                    }
                    await _context.SaveChangesAsync();
                }

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
                    CreatedAt = review.CreatedAt
                };

                return Ok(new { message = "Review created successfully", review = response });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating review for SearchHire {SearchHireId}", searchHireId);
                return StatusCode(500, new { message = "An error occurred while creating the review" });
            }
        }

        [HttpGet("expert/{expertId}")]
        public async Task<IActionResult> GetExpertReviews(int expertId)
        {
            try
            {
                // Verificar que el experto exista
                var expertExists = await _context.Users.AnyAsync(u => u.Id == expertId && u.Role == UserRole.Expert);
                if (!expertExists)
                {
                    return NotFound(new { message = "Expert not found" });
                }

                // Obtener todas las reseñas para el experto especificado
                var reviews = await _context.Reviews
                    .Where(r => r.ExpertId == expertId)
                    .Include(r => r.Reviewer)
                    .Include(r => r.ImagesCollection)
                    .Select(r => new ReviewResponseDto
                    {
                        Id = r.Id,
                        ReviewerId = r.ReviewerId,
                        ExpertId = r.ExpertId,
                        SearchHireId = r.SearchHireId,
                        Score = r.Score,
                        Description = r.Description,
                        ImageUrls = r.ImagesCollection.Select(ri => ri.ImageUrl).ToList(),
                        CreatedAt = r.CreatedAt
                    })
                    .ToListAsync();

                if (!reviews.Any())
                {
                    _logger.LogInformation("No reviews found for expert {ExpertId}", expertId);
                    return Ok(new { message = "No reviews found for this expert", reviews = new List<ReviewResponseDto>() });
                }

                return Ok(new { message = "Reviews retrieved successfully", reviews });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving reviews for expert {ExpertId}", expertId);
                return StatusCode(500, new { message = "An error occurred while retrieving reviews" });
            }
        }
    }
}