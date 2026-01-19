using System;

namespace newApi.DataLayer.Models.DTOs
{
    public class SearchListDto
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public int Frequency { get; set; }
        public bool IsActive { get; set; }
        public bool IsRevised { get; set; }
        public DateTime LastExecution { get; set; }
        public DateTime NextExecution { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime StartDate { get; set; }
        public string? LocationName { get; set; } // Nombre de la ubicación (ej: "Calle Juan Sadar, Soria")
        public int Category { get; set; }
        public string? CategoryName { get; set; } // ✅ NUEVO: Nombre de la categoría (ej: "Hogar", "Coches", "Motos")
        public UserDto User { get; set; }
        public SearchHireDto? SearchHire { get; set; }
        
        // Indicadores de notificaciones
        public int UnreadMessagesCount { get; set; } // Número de mensajes sin leer
        public bool HasPendingAppointment { get; set; } // Si hay una cita pendiente
        public string? PendingAppointmentStatus { get; set; } // Estado de la cita pendiente
        
        // ✅ NUEVO: Información del servicio y experto para cards
        public string? ServiceImageUrl { get; set; } // Primera imagen del servicio (si hay SearchHire con SearchService)
        public HomepageExpertAvailabilityDto? ExpertAvailability { get; set; } // Horario del experto
        public string? ExpertCity { get; set; } // Ciudad del experto (ej: "Madrid", "Barcelona")
    }


}