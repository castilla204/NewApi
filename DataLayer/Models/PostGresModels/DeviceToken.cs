using System;
using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Token de push (FCM) de un dispositivo de un usuario. Un usuario puede tener varios
    /// (móvil + tablet). El Token es único; al reinstalar/refrescar, se hace upsert.
    /// </summary>
    public class DeviceToken
    {
        [Key]
        public Guid Id { get; set; }
        public int UserId { get; set; }
        public string Token { get; set; } = string.Empty;
        public string Platform { get; set; } = "android"; // android | ios | web
        public DateTime CreatedAt { get; set; }
        public DateTime LastSeenAt { get; set; }
        public virtual User? User { get; set; }
    }
}
