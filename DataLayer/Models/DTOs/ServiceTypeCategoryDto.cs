namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para categorías de tipos de servicio
    /// </summary>
    public class ServiceTypeCategoryDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int Position { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// DTO para crear categorías de tipos de servicio
    /// </summary>
    public class CreateServiceTypeCategoryDto
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public int Position { get; set; } = 0;
        public bool IsActive { get; set; } = true;
    }

    /// <summary>
    /// DTO para actualizar categorías de tipos de servicio
    /// </summary>
    public class UpdateServiceTypeCategoryDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int Position { get; set; }
        public bool IsActive { get; set; }
    }
}
