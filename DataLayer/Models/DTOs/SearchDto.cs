using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace newApi.DataLayer.Models.DTOs
{
    public class SearchDto
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public int Frequency { get; set; }
        public bool IsActive { get; set; }
        public bool IsRevised { get; set; }
        public DateTime LastExecution { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime StartDate { get; set; }
        public int Category { get; set; }
        public SearchHireDto SearchHire { get; set; }
    }

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