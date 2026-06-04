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
            // 🛡️ Round 28 CUR-SEL-1: keys en lowercase explícito.
            // Program.cs:826 fuerza JsonSerializerOptions.PropertyNamingPolicy = null para todos
            // los endpoints, así que las propiedades de tipos anónimos PascalCase se serializan
            // como PascalCase. El frontend Currency interface usa lowercase (`code`, `name`, etc.),
            // así que el CurrencySelector descartaba TODOS los items tras el fetch
            // (c.code === undefined → filtrado out → dropdown vacío "que parece desaparecer").
            // Emitiendo keys en lowercase aquí matchea el contrato del frontend sin tocar política
            // JSON global.
            var supported = new[]
            {
                new { code = "EUR", name = "Euro",             symbol = "€",   locale = "es-ES" },
                new { code = "USD", name = "US Dollar",        symbol = "$",   locale = "en-US" },
                new { code = "GBP", name = "British Pound",    symbol = "£",   locale = "en-GB" },
                new { code = "CAD", name = "Canadian Dollar",  symbol = "CA$", locale = "en-CA" },
                new { code = "CHF", name = "Swiss Franc",      symbol = "CHF", locale = "de-CH" },
                new { code = "SEK", name = "Swedish Krona",    symbol = "kr",  locale = "sv-SE" },
                new { code = "DKK", name = "Danish Krone",     symbol = "kr",  locale = "da-DK" },
                new { code = "NOK", name = "Norwegian Krone",  symbol = "kr",  locale = "nb-NO" },
                new { code = "PLN", name = "Polish Złoty",     symbol = "zł",  locale = "pl-PL" },
                new { code = "HUF", name = "Hungarian Forint", symbol = "Ft",  locale = "hu-HU" },
                new { code = "CZK", name = "Czech Koruna",     symbol = "Kč",  locale = "cs-CZ" },
                new { code = "BGN", name = "Bulgarian Lev",    symbol = "лв",  locale = "bg-BG" },
                new { code = "RON", name = "Romanian Leu",     symbol = "lei", locale = "ro-RO" },
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
