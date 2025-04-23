using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace newApi.ScrapperGateway.DataLayer.Models.PostGresModels
{
    public class Notification
    {
        [Key]
        public Guid Id { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public string Type { get; set; }
        public string? Url { get; set; }
        public string? ImageUrl { get; set; }
        public int? UserId { get; set; }
        public bool Read { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ReadAt { get; set; }
        public virtual User User { get; set; }
    }
}
