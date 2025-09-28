namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para configuración de distribución de dinero
    /// </summary>
    public class MoneyDistributionConfigDto
    {
        public decimal ClientPercentage { get; set; }
        public decimal ExpertPercentage { get; set; }
        public decimal PlatformPercentage { get; set; }
        public string Source { get; set; } = string.Empty;
        public string? CategoryName { get; set; }
        public string? ServiceTypeCategoryName { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
