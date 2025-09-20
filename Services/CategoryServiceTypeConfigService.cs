using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    public interface ICategoryServiceTypeConfigService
    {
        Task<List<CategoryServiceTypeConfigDto>> GetCategoryServiceTypeConfigsAsync();
        Task<List<CategoryServiceTypeConfigDto>> GetConfigsByCategoryAsync(int categoryId);
        Task<List<CategoryServiceTypeConfigDto>> GetConfigsByServiceTypeAsync(int serviceTypeCategoryId);
        Task<CategoryServiceTypeConfigDto?> GetCategoryServiceTypeConfigAsync(int id);
        Task<CategoryServiceTypeConfigDto> CreateCategoryServiceTypeConfigAsync(CreateCategoryServiceTypeConfigDto dto);
        Task<CategoryServiceTypeConfigDto> UpdateCategoryServiceTypeConfigAsync(int id, CreateCategoryServiceTypeConfigDto dto);
        Task<bool> DeleteCategoryServiceTypeConfigAsync(int id);
    }

    public class CategoryServiceTypeConfigService : ICategoryServiceTypeConfigService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<CategoryServiceTypeConfigService> _logger;

        public CategoryServiceTypeConfigService(AppDbContext context, ILogger<CategoryServiceTypeConfigService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<List<CategoryServiceTypeConfigDto>> GetCategoryServiceTypeConfigsAsync()
        {
            var configs = await _context.CategoryServiceTypeConfigs
                .Include(cst => cst.Category)
                .Include(cst => cst.ServiceTypeCategory)
                .OrderBy(cst => cst.CategoryId)
                .ThenBy(cst => cst.ServiceTypeCategoryId)
                .ThenBy(cst => cst.Status)
                .ToListAsync();

            return configs.Select(MapToCategoryServiceTypeConfigDto).ToList();
        }

        public async Task<List<CategoryServiceTypeConfigDto>> GetConfigsByCategoryAsync(int categoryId)
        {
            var configs = await _context.CategoryServiceTypeConfigs
                .Include(cst => cst.Category)
                .Include(cst => cst.ServiceTypeCategory)
                .Where(cst => cst.CategoryId == categoryId)
                .OrderBy(cst => cst.ServiceTypeCategoryId)
                .ThenBy(cst => cst.Status)
                .ToListAsync();

            return configs.Select(MapToCategoryServiceTypeConfigDto).ToList();
        }

        public async Task<List<CategoryServiceTypeConfigDto>> GetConfigsByServiceTypeAsync(int serviceTypeCategoryId)
        {
            var configs = await _context.CategoryServiceTypeConfigs
                .Include(cst => cst.Category)
                .Include(cst => cst.ServiceTypeCategory)
                .Where(cst => cst.ServiceTypeCategoryId == serviceTypeCategoryId)
                .OrderBy(cst => cst.CategoryId)
                .ThenBy(cst => cst.Status)
                .ToListAsync();

            return configs.Select(MapToCategoryServiceTypeConfigDto).ToList();
        }

        public async Task<CategoryServiceTypeConfigDto?> GetCategoryServiceTypeConfigAsync(int id)
        {
            var config = await _context.CategoryServiceTypeConfigs
                .Include(cst => cst.Category)
                .Include(cst => cst.ServiceTypeCategory)
                .FirstOrDefaultAsync(cst => cst.Id == id);
            
            return config != null ? MapToCategoryServiceTypeConfigDto(config) : null;
        }

        public async Task<CategoryServiceTypeConfigDto> CreateCategoryServiceTypeConfigAsync(CreateCategoryServiceTypeConfigDto dto)
        {
            // Validar que los porcentajes sumen 100%
            if (dto.ClientPercentage + dto.ExpertPercentage + dto.PlatformPercentage != 100)
                throw new InvalidOperationException("Los porcentajes deben sumar 100%");

            // Verificar que no exista ya una configuración para esta combinación
            var existingConfig = await _context.CategoryServiceTypeConfigs
                .FirstOrDefaultAsync(cst => cst.CategoryId == dto.CategoryId 
                                         && cst.ServiceTypeCategoryId == dto.ServiceTypeCategoryId 
                                         && cst.Status == dto.Status);

            if (existingConfig != null)
                throw new InvalidOperationException("Ya existe una configuración para esta combinación de Category, ServiceTypeCategory y Status");

            var config = new CategoryServiceTypeConfig
            {
                CategoryId = dto.CategoryId,
                ServiceTypeCategoryId = dto.ServiceTypeCategoryId,
                Status = dto.Status,
                ClientPercentage = dto.ClientPercentage,
                ExpertPercentage = dto.ExpertPercentage,
                PlatformPercentage = dto.PlatformPercentage,
                IsActive = dto.IsActive,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.CategoryServiceTypeConfigs.Add(config);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Created category service type config: CategoryId={CategoryId}, ServiceTypeCategoryId={ServiceTypeCategoryId}, Status={Status} with percentages: Client={Client}%, Expert={Expert}%, Platform={Platform}%", 
                config.CategoryId, config.ServiceTypeCategoryId, config.Status, config.ClientPercentage, config.ExpertPercentage, config.PlatformPercentage);

            return await GetCategoryServiceTypeConfigAsync(config.Id) ?? throw new InvalidOperationException("Failed to retrieve created config");
        }

        public async Task<CategoryServiceTypeConfigDto> UpdateCategoryServiceTypeConfigAsync(int id, CreateCategoryServiceTypeConfigDto dto)
        {
            // Validar que los porcentajes sumen 100%
            if (dto.ClientPercentage + dto.ExpertPercentage + dto.PlatformPercentage != 100)
                throw new InvalidOperationException("Los porcentajes deben sumar 100%");

            var config = await _context.CategoryServiceTypeConfigs.FindAsync(id);
            if (config == null)
                throw new InvalidOperationException("Configuration not found");

            // Verificar que no exista ya una configuración para esta combinación (excluyendo la actual)
            var existingConfig = await _context.CategoryServiceTypeConfigs
                .FirstOrDefaultAsync(cst => cst.CategoryId == dto.CategoryId 
                                         && cst.ServiceTypeCategoryId == dto.ServiceTypeCategoryId 
                                         && cst.Status == dto.Status
                                         && cst.Id != id);

            if (existingConfig != null)
                throw new InvalidOperationException("Ya existe una configuración para esta combinación de Category, ServiceTypeCategory y Status");

            config.CategoryId = dto.CategoryId;
            config.ServiceTypeCategoryId = dto.ServiceTypeCategoryId;
            config.Status = dto.Status;
            config.ClientPercentage = dto.ClientPercentage;
            config.ExpertPercentage = dto.ExpertPercentage;
            config.PlatformPercentage = dto.PlatformPercentage;
            config.IsActive = dto.IsActive;
            config.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation("Updated category service type config: CategoryId={CategoryId}, ServiceTypeCategoryId={ServiceTypeCategoryId}, Status={Status} with percentages: Client={Client}%, Expert={Expert}%, Platform={Platform}%", 
                config.CategoryId, config.ServiceTypeCategoryId, config.Status, config.ClientPercentage, config.ExpertPercentage, config.PlatformPercentage);

            return await GetCategoryServiceTypeConfigAsync(config.Id) ?? throw new InvalidOperationException("Failed to retrieve updated config");
        }

        public async Task<bool> DeleteCategoryServiceTypeConfigAsync(int id)
        {
            var config = await _context.CategoryServiceTypeConfigs.FindAsync(id);
            if (config == null)
                return false;

            _context.CategoryServiceTypeConfigs.Remove(config);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Deleted category service type config: CategoryId={CategoryId}, ServiceTypeCategoryId={ServiceTypeCategoryId}, Status={Status}", 
                config.CategoryId, config.ServiceTypeCategoryId, config.Status);
            return true;
        }

        #region Private Methods

        private static CategoryServiceTypeConfigDto MapToCategoryServiceTypeConfigDto(CategoryServiceTypeConfig config)
        {
            return new CategoryServiceTypeConfigDto
            {
                Id = config.Id,
                CategoryId = config.CategoryId,
                CategoryName = config.Category?.Name ?? "Unknown",
                ServiceTypeCategoryId = config.ServiceTypeCategoryId,
                ServiceTypeCategoryName = config.ServiceTypeCategory?.Name ?? "Unknown",
                Status = config.Status,
                ClientPercentage = config.ClientPercentage,
                ExpertPercentage = config.ExpertPercentage,
                PlatformPercentage = config.PlatformPercentage,
                IsActive = config.IsActive,
                CreatedAt = config.CreatedAt,
                UpdatedAt = config.UpdatedAt
            };
        }

        #endregion
    }
}
