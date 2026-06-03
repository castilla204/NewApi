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

            // 🛡️ Round 28 CUR-3: alineado con SupportedCurrenciesList.cs (whitelist real de validación
            // backend). Antes esta lista expone LATAM/Asia (MXN/BRL/ARS/CLP/COP/JPY/CNY) que el backend
            // RECHAZARÍA con 400 si el usuario los seleccionara — además, no aparecían CAD/CHF/SEK/DKK/
            // NOK/PLN/HUF/CZK/BGN/RON que SÍ están permitidas. Ahora la lista coincide 1:1 con la
            // whitelist (EEA + EFTA + Anglo) — el dropdown muestra exactamente lo que el backend valida.
            var supported = new[]
            {
                new { Code = "EUR", Name = "Euro",             Symbol = "€",   Locale = "es-ES" },
                new { Code = "USD", Name = "US Dollar",        Symbol = "$",   Locale = "en-US" },
                new { Code = "GBP", Name = "British Pound",    Symbol = "£",   Locale = "en-GB" },
                new { Code = "CAD", Name = "Canadian Dollar",  Symbol = "CA$", Locale = "en-CA" },
                new { Code = "CHF", Name = "Swiss Franc",      Symbol = "CHF", Locale = "de-CH" },
                new { Code = "SEK", Name = "Swedish Krona",    Symbol = "kr",  Locale = "sv-SE" },
                new { Code = "DKK", Name = "Danish Krone",     Symbol = "kr",  Locale = "da-DK" },
                new { Code = "NOK", Name = "Norwegian Krone",  Symbol = "kr",  Locale = "nb-NO" },
                new { Code = "PLN", Name = "Polish Złoty",     Symbol = "zł",  Locale = "pl-PL" },
                new { Code = "HUF", Name = "Hungarian Forint", Symbol = "Ft",  Locale = "hu-HU" },
                new { Code = "CZK", Name = "Czech Koruna",     Symbol = "Kč",  Locale = "cs-CZ" },
                new { Code = "BGN", Name = "Bulgarian Lev",    Symbol = "лв",  Locale = "bg-BG" },
                new { Code = "RON", Name = "Romanian Leu",     Symbol = "lei", Locale = "ro-RO" },
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
