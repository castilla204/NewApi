using System.Collections.Generic;

namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// DTO completo para SearchDetails - incluye toda la información necesaria
    /// </summary>
    public class SearchDetailsCompleteDto
    {
        public SearchListDto Search { get; set; } = new SearchListDto();
        public MoneyDistributionConfigDto? MoneyDistribution { get; set; }
    }

    /// <summary>
    /// DTO para archivos entregables
    /// </summary>
    public class DeliverableDto
    {
        public int Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }


    /// <summary>
    /// DTO para conversaciones
    /// </summary>
    public class ConversationDto
    {
        public int Id { get; set; }
        public int UnreadCount { get; set; }
        public MessageDto? LastMessage { get; set; }
    }

    /// <summary>
    /// DTO para mensajes
    /// </summary>
    public class MessageDto
    {
        public int Id { get; set; }
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}
