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
        public UserDto User { get; set; }
        public SearchHireDto? SearchHire { get; set; }
        
        // Indicadores de notificaciones
        public int UnreadMessagesCount { get; set; } // Número de mensajes sin leer
        public bool HasPendingAppointment { get; set; } // Si hay una cita pendiente
        public string? PendingAppointmentStatus { get; set; } // Estado de la cita pendiente
    }


}