using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    public class MessageAttachment
    {
        [Key]
        public int Id { get; set; }
        public int MessageId { get; set; }
        public string Url { get; set; }
        public string ObjectName { get; set; }
        public string Type { get; set; } // "image", "video"
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public virtual Message Message { get; set; }
    }
}
