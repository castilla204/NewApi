using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace newApi.Services
{
    /// <summary>
    /// 💱 Multi-Currency Display — servicio de tipos de cambio para conversión multi-divisa en la UI.
    ///
    /// CONTEXTO
    /// --------
    /// La plataforma cobra en la moneda nativa del servicio (<c>SearchHire.Currency</c>, inmutable
    /// y ligada a Stripe). El frontend muestra precios también en la moneda preferida del usuario
    /// (whitelist EEA+US+CA+GB+CH+LATAM+APAC) como preview no vinculante. Este servicio expone las
    /// tasas más recientes para hacer la conversión sin pegarle a un proveedor externo en cada render.
    ///
    /// IMPLEMENTACIÓN
    /// --------------
    /// Backing primario: Frankfurter (https://api.frankfurter.app, ECB-backed, sin API key,
    /// publicado diariamente ~04:00 UTC). Refresco diario por Hangfire a las 06:00 UTC.
    ///
    /// Cadena de persistencia (defense-in-depth: una caída del proveedor NUNCA da 5xx al usuario):
    ///   1) IMemoryCache (TTL 24 h)
    ///   2) Snapshot persistido en tabla <c>ExchangeRateSnapshots</c> (cold-start / outage)
    ///   3) Fallback estático en código (último recurso, orden de magnitud razonable a 2026-05)
    ///
    /// El campo <see cref="ExchangeRatesResult.Source"/>/comportamiento equivalente indica si
    /// las tasas devueltas son "live", "cache", "db-snapshot" o "fallback" — útil para diagnóstico
    /// si el frontend quiere advertir de tasas potencialmente desactualizadas.
    /// </summary>
    public interface IExchangeRateService
    {
        /// <summary>
        /// Devuelve el factor multiplicador <c>fromCurrency → toCurrency</c>.
        /// <para>Para convertir: <c>amount_to = amount_from * GetRateAsync(from, to)</c>.</para>
        /// Si <paramref name="fromCurrency"/> == <paramref name="toCurrency"/> devuelve 1.0 sin tocar
        /// cache ni red. Las dos divisas se normalizan a mayúsculas (ISO 4217 alfa-3).
        /// </summary>
        /// <param name="fromCurrency">Código ISO 4217 origen (ej. "EUR").</param>
        /// <param name="toCurrency">Código ISO 4217 destino (ej. "USD").</param>
        /// <param name="asOf">
        /// Reservado para lookup histórico (snapshot persistido a esa fecha). Si <c>null</c> usa la
        /// tasa vigente. En esta primera versión solo se respeta cuando se solicita explícitamente y
        /// existe snapshot ese día; en otro caso cae al snapshot más reciente.
        /// </param>
        /// <remarks>
        /// NO lanza ante outage: si tras agotar cache+BD+fallback no hay una tasa razonable para el
        /// par solicitado, devuelve 1.0 como degradación segura (el caller verá que from==to o lo
        /// equivalente y puede decidir ocultar la conversión).
        /// </remarks>
        Task<decimal> GetRateAsync(string fromCurrency, string toCurrency, System.DateTime? asOf = null);

        /// <summary>
        /// Devuelve un diccionario {códigoISO → tasa} relativas a <paramref name="baseCurrency"/>.
        /// Las claves son ISO 4217 en MAYÚSCULAS (EUR, USD, GBP, …). El valor para la propia
        /// base es 1.0. El método NUNCA lanza: ante cualquier fallo, cae al fallback estático.
        /// </summary>
        Task<IReadOnlyDictionary<string, decimal>> GetAllRatesAsync(string baseCurrency, CancellationToken ct = default);

        /// <summary>
        /// Overload sin CT y con base por defecto "EUR" — la firma exacta que pide el plan de
        /// Multi-Currency Display. Devuelve un <see cref="Dictionary{TKey,TValue}"/> mutable
        /// (copia del snapshot) para encajar con consumidores que esperan ese tipo concreto.
        /// </summary>
        Task<Dictionary<string, decimal>> GetAllRatesAsync(string baseCurrency = "EUR");

        /// <summary>
        /// Punto de entrada del job Hangfire recurrente diario. Fetch desde Frankfurter, persiste
        /// snapshot en BD (<c>ExchangeRateSnapshots</c>) y refresca el IMemoryCache.
        /// <para>
        /// Idempotente y a prueba de fallos: dos ejecuciones el mismo día crean dos snapshots
        /// (queremos histórico de cuándo refrescamos), pero la cache queda con la última. Si el
        /// fetch falla, se logea como warning y la cache anterior queda intacta — el sistema sigue
        /// sirviendo las tasas previas y el digest de Hangfire avisará al admin si el job se cae
        /// repetidamente.
        /// </para>
        /// </summary>
        Task RefreshRatesAsync();
    }
}
