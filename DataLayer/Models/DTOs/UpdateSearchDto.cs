using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.DTOs
{
    public class UpdateSearchDto
    {
        [Required]
        public string Title { get; set; }
        public string? Description { get; set; }
        [Required]
        public int Frequency { get; set; }
        public DateTime StartDate { get; set; }
    }
}