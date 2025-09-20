namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para mostrar configuración de porcentajes por estado de cita
    /// </summary>
    public class AppointmentStatusConfigDto
    {
        public int Id { get; set; }
        public string Status { get; set; } = string.Empty;
        public decimal ClientPercentage { get; set; }
        public decimal ExpertPercentage { get; set; }
        public decimal PlatformPercentage { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// DTO para crear/actualizar configuración de porcentajes por estado de cita
    /// </summary>
    public class CreateAppointmentStatusConfigDto
    {
        public string Status { get; set; } = string.Empty;
        public decimal ClientPercentage { get; set; }
        public decimal ExpertPercentage { get; set; }
        public decimal PlatformPercentage { get; set; }
        public bool IsActive { get; set; } = true;
    }

    /// <summary>
    /// DTO para mostrar configuración de porcentajes por categoría de servicio
    /// </summary>
    public class ServiceTypeCategoryConfigDto
    {
        public int Id { get; set; }
        public int ServiceTypeCategoryId { get; set; }
        public string ServiceTypeCategoryName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal ClientPercentage { get; set; }
        public decimal ExpertPercentage { get; set; }
        public decimal PlatformPercentage { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// DTO para crear/actualizar configuración de porcentajes por categoría de servicio
    /// </summary>
    public class CreateServiceTypeCategoryConfigDto
    {
        public int ServiceTypeCategoryId { get; set; }
        public string Status { get; set; } = string.Empty;
        public decimal ClientPercentage { get; set; }
        public decimal ExpertPercentage { get; set; }
        public decimal PlatformPercentage { get; set; }
        public bool IsActive { get; set; } = true;
    }

    /// <summary>
    /// DTO para mostrar configuración de porcentajes por combinación Category + ServiceTypeCategory
    /// </summary>
    public class CategoryServiceTypeConfigDto
    {
        public int Id { get; set; }
        public int CategoryId { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public int ServiceTypeCategoryId { get; set; }
        public string ServiceTypeCategoryName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal ClientPercentage { get; set; }
        public decimal ExpertPercentage { get; set; }
        public decimal PlatformPercentage { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// DTO para crear/actualizar configuración de porcentajes por combinación Category + ServiceTypeCategory
    /// </summary>
    public class CreateCategoryServiceTypeConfigDto
    {
        public int CategoryId { get; set; }
        public int ServiceTypeCategoryId { get; set; }
        public string Status { get; set; } = string.Empty;
        public decimal ClientPercentage { get; set; }
        public decimal ExpertPercentage { get; set; }
        public decimal PlatformPercentage { get; set; }
        public bool IsActive { get; set; } = true;
    }

    /// <summary>
    /// DTO para mostrar la configuración de distribución de dinero
    /// </summary>
    public class MoneyDistributionConfigDto
    {
        public decimal ClientPercentage { get; set; }
        public decimal ExpertPercentage { get; set; }
        public decimal PlatformPercentage { get; set; }
        public string Source { get; set; } = string.Empty; // "category_service_type", "service_type_category", "appointment_status", "default"
        public string? CategoryName { get; set; }
        public string? ServiceTypeCategoryName { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
