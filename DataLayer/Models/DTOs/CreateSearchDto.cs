using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.DTOs
{
    public class CreateSearchDto
    {
        public int Frequency { get; set; }
        public string Title { get; set; }
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        public DateTime StartDate { get; set; }

    }


}
