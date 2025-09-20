using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    public interface IAppointmentConfigService
    {
        Task<List<AppointmentStatusConfigDto>> GetAppointmentStatusConfigsAsync();
        Task<AppointmentStatusConfigDto?> GetAppointmentStatusConfigAsync(int id);
        Task<AppointmentStatusConfigDto> CreateAppointmentStatusConfigAsync(CreateAppointmentStatusConfigDto dto);
        Task<AppointmentStatusConfigDto> UpdateAppointmentStatusConfigAsync(int id, CreateAppointmentStatusConfigDto dto);
        Task<bool> DeleteAppointmentStatusConfigAsync(int id);
        
        Task<List<ServiceTypeCategoryConfigDto>> GetServiceTypeCategoryConfigsAsync();
        Task<List<ServiceTypeCategoryConfigDto>> GetServiceTypeCategoryConfigsByCategoryAsync(int serviceTypeCategoryId);
        Task<ServiceTypeCategoryConfigDto?> GetServiceTypeCategoryConfigAsync(int id);
        Task<ServiceTypeCategoryConfigDto> CreateServiceTypeCategoryConfigAsync(CreateServiceTypeCategoryConfigDto dto);
        Task<ServiceTypeCategoryConfigDto> UpdateServiceTypeCategoryConfigAsync(int id, CreateServiceTypeCategoryConfigDto dto);
        Task<bool> DeleteServiceTypeCategoryConfigAsync(int id);
        
        Task<MoneyDistributionConfigDto?> GetMoneyDistributionConfigAsync(string status, int? categoryId, int? serviceTypeCategoryId);
    }

    public class AppointmentConfigService : IAppointmentConfigService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<AppointmentConfigService> _logger;

        public AppointmentConfigService(AppDbContext context, ILogger<AppointmentConfigService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<List<AppointmentStatusConfigDto>> GetAppointmentStatusConfigsAsync()
        {
            var configs = await _context.AppointmentStatusConfigs
                .OrderBy(c => c.Status)
                .ToListAsync();

            return configs.Select(MapToAppointmentStatusConfigDto).ToList();
        }

        public async Task<AppointmentStatusConfigDto?> GetAppointmentStatusConfigAsync(int id)
        {
            var config = await _context.AppointmentStatusConfigs.FindAsync(id);
            return config != null ? MapToAppointmentStatusConfigDto(config) : null;
        }

        public async Task<AppointmentStatusConfigDto> CreateAppointmentStatusConfigAsync(CreateAppointmentStatusConfigDto dto)
        {
            // Validar que los porcentajes sumen 100%
            if (dto.ClientPercentage + dto.ExpertPercentage + dto.PlatformPercentage != 100)
                throw new InvalidOperationException("Los porcentajes deben sumar 100%");

            var config = new AppointmentStatusConfig
            {
                Status = dto.Status,
                ClientPercentage = dto.ClientPercentage,
                ExpertPercentage = dto.ExpertPercentage,
                PlatformPercentage = dto.PlatformPercentage,
                IsActive = dto.IsActive,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.AppointmentStatusConfigs.Add(config);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Created appointment status config: {Status} with percentages: Client={Client}%, Expert={Expert}%, Platform={Platform}%", 
                config.Status, config.ClientPercentage, config.ExpertPercentage, config.PlatformPercentage);

            return MapToAppointmentStatusConfigDto(config);
        }

        public async Task<AppointmentStatusConfigDto> UpdateAppointmentStatusConfigAsync(int id, CreateAppointmentStatusConfigDto dto)
        {
            // Validar que los porcentajes sumen 100%
            if (dto.ClientPercentage + dto.ExpertPercentage + dto.PlatformPercentage != 100)
                throw new InvalidOperationException("Los porcentajes deben sumar 100%");

            var config = await _context.AppointmentStatusConfigs.FindAsync(id);
            if (config == null)
                throw new InvalidOperationException("Configuration not found");

            config.Status = dto.Status;
            config.ClientPercentage = dto.ClientPercentage;
            config.ExpertPercentage = dto.ExpertPercentage;
            config.PlatformPercentage = dto.PlatformPercentage;
            config.IsActive = dto.IsActive;
            config.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation("Updated appointment status config: {Status} with percentages: Client={Client}%, Expert={Expert}%, Platform={Platform}%", 
                config.Status, config.ClientPercentage, config.ExpertPercentage, config.PlatformPercentage);

            return MapToAppointmentStatusConfigDto(config);
        }

        public async Task<bool> DeleteAppointmentStatusConfigAsync(int id)
        {
            var config = await _context.AppointmentStatusConfigs.FindAsync(id);
            if (config == null)
                return false;

            _context.AppointmentStatusConfigs.Remove(config);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Deleted appointment status config: {Status}", config.Status);
            return true;
        }

        public async Task<List<ServiceTypeCategoryConfigDto>> GetServiceTypeCategoryConfigsAsync()
        {
            var configs = await _context.ServiceTypeCategoryConfigs
                .Include(sc => sc.ServiceTypeCategory)
                .OrderBy(c => c.ServiceTypeCategoryId)
                .ThenBy(c => c.Status)
                .ToListAsync();

            return configs.Select(MapToServiceTypeCategoryConfigDto).ToList();
        }

        public async Task<List<ServiceTypeCategoryConfigDto>> GetServiceTypeCategoryConfigsByCategoryAsync(int serviceTypeCategoryId)
        {
            var configs = await _context.ServiceTypeCategoryConfigs
                .Include(sc => sc.ServiceTypeCategory)
                .Where(sc => sc.ServiceTypeCategoryId == serviceTypeCategoryId)
                .OrderBy(c => c.Status)
                .ToListAsync();

            return configs.Select(MapToServiceTypeCategoryConfigDto).ToList();
        }

        public async Task<ServiceTypeCategoryConfigDto?> GetServiceTypeCategoryConfigAsync(int id)
        {
            var config = await _context.ServiceTypeCategoryConfigs
                .Include(sc => sc.ServiceTypeCategory)
                .FirstOrDefaultAsync(sc => sc.Id == id);
            
            return config != null ? MapToServiceTypeCategoryConfigDto(config) : null;
        }

        public async Task<ServiceTypeCategoryConfigDto> CreateServiceTypeCategoryConfigAsync(CreateServiceTypeCategoryConfigDto dto)
        {
            // Validar que los porcentajes sumen 100%
            if (dto.ClientPercentage + dto.ExpertPercentage + dto.PlatformPercentage != 100)
                throw new InvalidOperationException("Los porcentajes deben sumar 100%");

            var config = new ServiceTypeCategoryConfig
            {
                ServiceTypeCategoryId = dto.ServiceTypeCategoryId,
                Status = dto.Status,
                ClientPercentage = dto.ClientPercentage,
                ExpertPercentage = dto.ExpertPercentage,
                PlatformPercentage = dto.PlatformPercentage,
                IsActive = dto.IsActive,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.ServiceTypeCategoryConfigs.Add(config);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Created service type category config: CategoryId={CategoryId}, Status={Status} with percentages: Client={Client}%, Expert={Expert}%, Platform={Platform}%", 
                config.ServiceTypeCategoryId, config.Status, config.ClientPercentage, config.ExpertPercentage, config.PlatformPercentage);

            return await GetServiceTypeCategoryConfigAsync(config.Id) ?? throw new InvalidOperationException("Failed to retrieve created config");
        }

        public async Task<ServiceTypeCategoryConfigDto> UpdateServiceTypeCategoryConfigAsync(int id, CreateServiceTypeCategoryConfigDto dto)
        {
            // Validar que los porcentajes sumen 100%
            if (dto.ClientPercentage + dto.ExpertPercentage + dto.PlatformPercentage != 100)
                throw new InvalidOperationException("Los porcentajes deben sumar 100%");

            var config = await _context.ServiceTypeCategoryConfigs.FindAsync(id);
            if (config == null)
                throw new InvalidOperationException("Configuration not found");

            config.ServiceTypeCategoryId = dto.ServiceTypeCategoryId;
            config.Status = dto.Status;
            config.ClientPercentage = dto.ClientPercentage;
            config.ExpertPercentage = dto.ExpertPercentage;
            config.PlatformPercentage = dto.PlatformPercentage;
            config.IsActive = dto.IsActive;
            config.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation("Updated service type category config: CategoryId={CategoryId}, Status={Status} with percentages: Client={Client}%, Expert={Expert}%, Platform={Platform}%", 
                config.ServiceTypeCategoryId, config.Status, config.ClientPercentage, config.ExpertPercentage, config.PlatformPercentage);

            return await GetServiceTypeCategoryConfigAsync(config.Id) ?? throw new InvalidOperationException("Failed to retrieve updated config");
        }

        public async Task<bool> DeleteServiceTypeCategoryConfigAsync(int id)
        {
            var config = await _context.ServiceTypeCategoryConfigs.FindAsync(id);
            if (config == null)
                return false;

            _context.ServiceTypeCategoryConfigs.Remove(config);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Deleted service type category config: CategoryId={CategoryId}, Status={Status}", 
                config.ServiceTypeCategoryId, config.Status);
            return true;
        }

        public async Task<MoneyDistributionConfigDto?> GetMoneyDistributionConfigAsync(string status, int? categoryId, int? serviceTypeCategoryId)
        {
            // 1. Buscar configuración específica por Category + ServiceTypeCategory
            if (categoryId.HasValue && serviceTypeCategoryId.HasValue)
            {
                var specificConfig = await _context.CategoryServiceTypeConfigs
                    .Include(cst => cst.Category)
                    .Include(cst => cst.ServiceTypeCategory)
                    .FirstOrDefaultAsync(cst => cst.CategoryId == categoryId.Value 
                                             && cst.ServiceTypeCategoryId == serviceTypeCategoryId.Value 
                                             && cst.Status == status 
                                             && cst.IsActive);

                if (specificConfig != null)
                {
                    return new MoneyDistributionConfigDto
                    {
                        ClientPercentage = specificConfig.ClientPercentage,
                        ExpertPercentage = specificConfig.ExpertPercentage,
                        PlatformPercentage = specificConfig.PlatformPercentage,
                        Source = "category_service_type",
                        CategoryName = specificConfig.Category?.Name,
                        ServiceTypeCategoryName = specificConfig.ServiceTypeCategory?.Name,
                        Status = status
                    };
                }
            }

            // 2. Buscar configuración específica por ServiceTypeCategory
            if (serviceTypeCategoryId.HasValue)
            {
                var categoryConfig = await _context.ServiceTypeCategoryConfigs
                    .Include(sc => sc.ServiceTypeCategory)
                    .FirstOrDefaultAsync(sc => sc.ServiceTypeCategoryId == serviceTypeCategoryId.Value 
                                             && sc.Status == status 
                                             && sc.IsActive);

                if (categoryConfig != null)
                {
                    return new MoneyDistributionConfigDto
                    {
                        ClientPercentage = categoryConfig.ClientPercentage,
                        ExpertPercentage = categoryConfig.ExpertPercentage,
                        PlatformPercentage = categoryConfig.PlatformPercentage,
                        Source = "service_type_category",
                        ServiceTypeCategoryName = categoryConfig.ServiceTypeCategory?.Name,
                        Status = status
                    };
                }
            }

            // 3. Buscar configuración por defecto por estado
            var defaultConfig = await _context.AppointmentStatusConfigs
                .FirstOrDefaultAsync(ac => ac.Status == status && ac.IsActive);

            if (defaultConfig != null)
            {
                return new MoneyDistributionConfigDto
                {
                    ClientPercentage = defaultConfig.ClientPercentage,
                    ExpertPercentage = defaultConfig.ExpertPercentage,
                    PlatformPercentage = defaultConfig.PlatformPercentage,
                    Source = "appointment_status",
                    Status = status
                };
            }

            return null;
        }

        #region Private Methods

        private static AppointmentStatusConfigDto MapToAppointmentStatusConfigDto(AppointmentStatusConfig config)
        {
            return new AppointmentStatusConfigDto
            {
                Id = config.Id,
                Status = config.Status,
                ClientPercentage = config.ClientPercentage,
                ExpertPercentage = config.ExpertPercentage,
                PlatformPercentage = config.PlatformPercentage,
                IsActive = config.IsActive,
                CreatedAt = config.CreatedAt,
                UpdatedAt = config.UpdatedAt
            };
        }

        private static ServiceTypeCategoryConfigDto MapToServiceTypeCategoryConfigDto(ServiceTypeCategoryConfig config)
        {
            return new ServiceTypeCategoryConfigDto
            {
                Id = config.Id,
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
