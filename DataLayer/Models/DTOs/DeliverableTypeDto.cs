namespace newApi.DataLayer.Models.DTOs
{
    public class DeliverableTypeDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string? Description { get; set; }
        public bool IsRequired { get; set; }
        public bool IsActive { get; set; }
        public int SortOrder { get; set; }
    }
}

