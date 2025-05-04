using System;
using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.DTOs
{
    public class SearchResultDto
    {
        [Key]
        public int Id { get; set; }
        public int SearchId { get; set; }
        public string AdId { get; set; }
        public DateTime FoundAt { get; set; }

    }
}
