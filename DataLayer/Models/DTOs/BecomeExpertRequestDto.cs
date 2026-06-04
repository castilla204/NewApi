using Microsoft.AspNetCore.Http;

namespace newApi.ScrapperGateway.DataLayer.Models.DTOs
{
    public class BecomeExpertRequestDto
    {
        // 🛡️ Round 28 MUD-AG: nullable para soportar re-onboarding tras mudanza
        // (el experto puede conservar su foto del país anterior sin re-uploadear).
        public IFormFile? ProfilePicture { get; set; }
        public string Description { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }
        
        /// <summary>
        /// Días de la semana en los que el experto trabaja (múltiples valores permitidos en form-data)
        /// Valores permitidos: "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
        /// </summary>
        public List<string>? AvailabilityDaysOfWeek { get; set; }
        
        /// <summary>
        /// Hora de inicio de la franja horaria (formato "HH:mm", ej: "09:00")
        /// </summary>
        public string? AvailabilityStartTime { get; set; }
        
        /// <summary>
        /// Hora de fin de la franja horaria (formato "HH:mm", ej: "18:00")
        /// </summary>
        public string? AvailabilityEndTime { get; set; }
    }

    public class UpdateExpertProfileRequestDto
    {
        public IFormFile? ProfilePicture { get; set; }
        public string Description { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }
        
        /// <summary>
        /// Días de la semana en los que el experto trabaja (múltiples valores permitidos en form-data)
        /// Valores permitidos: "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
        /// Si se proporciona, actualiza la disponibilidad del experto
        /// </summary>
        public List<string>? AvailabilityDaysOfWeek { get; set; }
        
        /// <summary>
        /// Hora de inicio de la franja horaria (formato "HH:mm", ej: "09:00")
        /// </summary>
        public string? AvailabilityStartTime { get; set; }
        
        /// <summary>
        /// Hora de fin de la franja horaria (formato "HH:mm", ej: "18:00")
        /// </summary>
        public string? AvailabilityEndTime { get; set; }
    }
}