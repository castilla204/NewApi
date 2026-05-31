// In newapi/DataLayer/Models/DTOs/SearchServiceDto.cs
namespace newApi.DataLayer.Models.DTOs
{
    public class SearchServiceDto
    {
        public int Id { get; set; }
        public int CategoryId { get; set; }
        public decimal Price { get; set; }
        /// <summary>
        /// ISO 4217 currency code for <see cref="Price"/>. Defaults to "EUR".
        /// Frontend should format prices using Intl.NumberFormat(locale, { style: 'currency', currency }).
        /// </summary>
        public string Currency { get; set; } = "EUR";
        public string Conditions { get; set; }
        public int DurationInHours { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<string> ImageUrls { get; set; } = new List<string>();
    }
}
