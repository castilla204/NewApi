using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// 💱 Multi-Currency Display: snapshot diario de tasas de cambio publicadas por Frankfurter/ECB.
    ///
    /// Modelo display-only — la moneda de cobro real sigue siendo <see cref="SearchHire.Currency"/>
    /// (inmutable, ligada a Stripe). Estas tasas se usan para previsualizar precios en la moneda
    /// preferida del visitante (catálogo/búsqueda/detalle) y para el preview de checkout junto al
    /// importe real en la moneda del servicio.
    ///
    /// La cadena de persistencia (job Hangfire diario 06:00 UTC → ECB publica ~04:00 UTC):
    ///  - Primario: fetch desde Frankfurter (https://api.frankfurter.app/latest?from=EUR)
    ///  - Cache: <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/> con TTL 24 h.
    ///  - Fallback: lectura del último snapshot persistido (cold-start / outage del proveedor).
    ///
    /// El campo <see cref="RatesJson"/> es JSONB en Postgres con el shape exacto del API:
    /// {"USD": 1.0921, "GBP": 0.8543, ...} — claves ISO 4217 alfa-3 mayúsculas.
    /// </summary>
    [Table("ExchangeRateSnapshots")]
    public class ExchangeRateSnapshot
    {
        /// <summary>PK auto-increment. BIGSERIAL en Postgres.</summary>
        public long Id { get; set; }

        /// <summary>
        /// Moneda base ISO 4217 (típicamente "EUR"). Las tasas en <see cref="RatesJson"/> son
        /// multiplicadores: amount_target = amount_base * rates[target].
        /// </summary>
        [Required]
        [MaxLength(3)]
        [Column(TypeName = "char(3)")]
        public string BaseCurrency { get; set; } = "EUR";

        /// <summary>
        /// JSONB con el mapa código → tasa. Persistido como string para evitar acoplar el modelo
        /// a un tipo concreto de Postgres (System.Text.Json hace serialización in/out).
        /// </summary>
        [Required]
        [Column(TypeName = "jsonb")]
        public string RatesJson { get; set; } = "{}";

        /// <summary>
        /// Identificador del proveedor que sirvió el snapshot (ej. "frankfurter", "openexchangerates",
        /// "fallback"). Útil para auditoría cuando saltamos al secundario tras outage del primario.
        /// </summary>
        [Required]
        [MaxLength(64)]
        public string Source { get; set; } = "frankfurter";

        /// <summary>
        /// Fecha efectiva publicada por el proveedor (ECB la publica una vez al día sobre las 16:00 CET).
        /// Distinta de <see cref="FetchedAt"/>: si el ECB no publicó (fin de semana/festivo) reutilizamos
        /// la última fecha hábil y aquí guardamos esa fecha, no la del fetch.
        /// </summary>
        public DateOnly RateDate { get; set; }

        /// <summary>Momento UTC en que este registro se persistió desde el proveedor.</summary>
        public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
    }
}
