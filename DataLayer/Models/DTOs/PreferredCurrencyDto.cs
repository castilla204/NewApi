using System.ComponentModel.DataAnnotations;

namespace newApi.DataLayer.Models.DTOs
{
    /// <summary>
    /// 🌍 Round 22: Payload del endpoint POST /api/user/preferred-currency.
    /// El cliente envía un código ISO 4217 de 3 letras (case-insensitive); el backend
    /// lo valida contra <c>SupportedCurrenciesList</c> y lo persiste en mayúsculas.
    /// </summary>
    public class PreferredCurrencyDto
    {
        [Required]
        [StringLength(3, MinimumLength = 3, ErrorMessage = "Currency must be a 3-letter ISO code")]
        public string Currency { get; set; } = string.Empty;
    }
}
