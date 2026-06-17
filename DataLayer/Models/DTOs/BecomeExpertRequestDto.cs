using Microsoft.AspNetCore.Http;

namespace newApi.ScrapperGateway.DataLayer.Models.DTOs
{
    public class BecomeExpertRequestDto
    {
        // 🛡️ Round 28 MUD-AG: nullable para soportar re-onboarding tras mudanza
        // (el experto puede conservar su foto del país anterior sin re-uploadear).
        public IFormFile? ProfilePicture { get; set; }
        public string Description { get; set; }
        /// <summary>Formación opcional del experto (JSON de items). Se muestra al cliente.</summary>
        public string? Formacion { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }

        /// <summary>
        /// Radio de trabajo en km desde el punto fijo del experto (0 = solo en su taller, máx 200).
        /// Opcional al registrarse: si no se envía, se usa el default (100 km).
        /// </summary>
        public int? WorkRadiusKm { get; set; }

        /// <summary>Puerta/garaje del taller (solo modo fijo). Opcional.</summary>
        public string? WorkLocationDoor { get; set; }
        /// <summary>Piso/planta del taller (solo modo fijo). Opcional.</summary>
        public string? WorkLocationFloor { get; set; }
        /// <summary>Observaciones de acceso al taller (solo modo fijo). Opcional.</summary>
        public string? WorkLocationDetails { get; set; }

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
        /// <summary>Formación opcional del experto (JSON de items). Se muestra al cliente.</summary>
        public string? Formacion { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }

        /// <summary>
        /// Radio de trabajo en km desde el punto fijo del experto (0 = solo en su taller, máx 200).
        /// Opcional: si no se envía, se conserva el valor actual.
        /// </summary>
        public int? WorkRadiusKm { get; set; }

        /// <summary>Puerta/garaje del taller (solo modo fijo). Opcional. Si modo rango, se ignora/limpia.</summary>
        public string? WorkLocationDoor { get; set; }
        /// <summary>Piso/planta del taller (solo modo fijo). Opcional.</summary>
        public string? WorkLocationFloor { get; set; }
        /// <summary>Observaciones de acceso al taller (solo modo fijo). Opcional.</summary>
        public string? WorkLocationDetails { get; set; }

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