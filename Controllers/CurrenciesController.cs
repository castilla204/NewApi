using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using newApi.Services;

namespace newApi.Controllers
{
    /// <summary>
    /// Endpoint público con la whitelist de divisas soportadas por la UI y las tasas
    /// de cambio actuales relativas a EUR. Pensado para alimentar el selector de
    /// divisa del frontend (CurrencyContext) en un único round-trip al cargar la app.
    ///
    /// El controller no requiere autenticación: las tasas son información pública y
    /// el servicio backing está cacheado (ver <see cref="ExchangeRateService"/>) para
    /// no convertir un endpoint anónimo en vector de abuso del proveedor upstream.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class CurrenciesController : ControllerBase
    {
        private readonly IExchangeRateService _rates;

        public CurrenciesController(IExchangeRateService rates)
        {
            _rates = rates;
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> GetSupported(CancellationToken ct)
        {
            var rates = await _rates.GetAllRatesAsync("EUR", ct).ConfigureAwait(false);

            var supported = new[]
            {
                new { Code = "EUR", Name = "Euro",             Symbol = "€",  Locale = "es-ES" },
                new { Code = "USD", Name = "US Dollar",        Symbol = "$",  Locale = "en-US" },
                new { Code = "GBP", Name = "British Pound",    Symbol = "£",  Locale = "en-GB" },
                new { Code = "MXN", Name = "Peso Mexicano",    Symbol = "$",  Locale = "es-MX" },
                new { Code = "BRL", Name = "Real",             Symbol = "R$", Locale = "pt-BR" },
                new { Code = "ARS", Name = "Peso Argentino",   Symbol = "$",  Locale = "es-AR" },
                new { Code = "CLP", Name = "Peso Chileno",     Symbol = "$",  Locale = "es-CL" },
                new { Code = "COP", Name = "Peso Colombiano",  Symbol = "$",  Locale = "es-CO" },
                new { Code = "JPY", Name = "Yen",              Symbol = "¥",  Locale = "ja-JP" },
                new { Code = "CNY", Name = "Yuan",             Symbol = "¥",  Locale = "zh-CN" },
            };

            return Ok(new
            {
                currencies = supported,
                rates,
                baseCurrency = "EUR",
                lastUpdated = DateTime.UtcNow
            });
        }
    }
}
