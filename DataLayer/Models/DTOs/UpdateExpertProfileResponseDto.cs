namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO para la respuesta de UpdateExpertProfile
    /// </summary>
    public class UpdateExpertProfileResponseDto
    {
        public string Message { get; set; }
        public ExpertProfileDto ExpertProfile { get; set; }
    }
}

