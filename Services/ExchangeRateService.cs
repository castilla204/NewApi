using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    /// <summary>
    /// Implementación de <see cref="IExchangeRateService"/>.
    ///
    /// Capas activas (defense-in-depth — un outage del proveedor NUNCA da 5xx al usuario):
    ///   1) IMemoryCache (TTL 24h, refrescado por <see cref="RefreshRatesAsync"/> vía Hangfire).
    ///   2) Último snapshot persistido en <c>ExchangeRateSnapshots</c> (cold-start tras reinicio).
    ///   3) Fallback estático en código (último recurso, orden de magnitud razonable a 2026-05).
    ///
    /// Cadena de escritura: job Hangfire diario 06:00 UTC → fetch Frankfurter → insert snapshot →
    /// set IMemoryCache. Idempotente: dos ejecuciones el mismo día crean dos snapshots (queremos
    /// histórico de cuándo refrescamos), pero la cache queda con la última.
    /// </summary>
    public sealed class ExchangeRateService : IExchangeRateService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly AppDbContext _context;
        private readonly ILogger<ExchangeRateService> _logger;

        // Frankfurter: API pública sin API key, datos del ECB publicados diariamente ~16:00 CET.
        // Endpoint: {"base":"EUR","date":"2026-05-30","rates":{"USD":1.08,...}}.
        // El cliente nombrado "frankfurter" trae BaseAddress fija; aquí solo pasamos el path relativo.
        private const string FrankfurterClientName = "frankfurter";
        private const string FrankfurterLatestPath = "latest?from={0}";
        // Conservamos la URL absoluta como fallback si el cliente nombrado no se ha registrado
        // (escenarios de tests unitarios que instancian el servicio a pelo).
        private const string FrankfurterAbsoluteUrlTemplate = "https://api.frankfurter.app/latest?from={0}";
        private const string ProviderId = "frankfurter";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
        private static readonly TimeSpan FallbackCacheTtl = TimeSpan.FromMinutes(5);

        // Fallback estático con orden de magnitud razonable a 2026-05. Solo se usa si
        // upstream falla Y no hay caché previa. Mejor servir tasas "casi correctas" que
        // romper la UI por completo.
        private static readonly IReadOnlyDictionary<string, decimal> StaticFallbackEurBase =
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["EUR"] = 1.00m,
                ["USD"] = 1.08m,
                ["GBP"] = 0.85m,
                ["MXN"] = 18.50m,
                ["BRL"] = 5.50m,
                ["ARS"] = 980.00m,
                ["CLP"] = 1020.00m,
                ["COP"] = 4250.00m,
                ["JPY"] = 168.00m,
                ["CNY"] = 7.80m,
                ["CHF"] = 0.95m,
                ["CAD"] = 1.48m,
            };

        public ExchangeRateService(
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            AppDbContext context,
            ILogger<ExchangeRateService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _context = context;
            _logger = logger;
        }

        // -------------------------------------------------------------------------
        // GetRateAsync: factor multiplicador from→to. Degrada a 1.0 ante imposibilidad.
        // -------------------------------------------------------------------------
        public async Task<decimal> GetRateAsync(string fromCurrency, string toCurrency, DateTime? asOf = null)
        {
            var from = Normalize(fromCurrency);
            var to = Normalize(toCurrency);
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                return 1.0m;
            }

            // Lookup histórico best-effort: si se pide una fecha concreta y existe snapshot ese día
            // lo usamos; si no, caemos a la tasa vigente (mejor preview aproximado que error).
            if (asOf.HasValue)
            {
                var historical = await TryLoadHistoricalRatesAsync(from, asOf.Value).ConfigureAwait(false);
                if (historical != null && historical.TryGetValue(to, out var historicalRate) && historicalRate > 0m)
                {
                    return historicalRate;
                }
            }

            try
            {
                var rates = await GetAllRatesInternalAsync(from, CancellationToken.None).ConfigureAwait(false);
                if (rates.TryGetValue(to, out var rate) && rate > 0m)
                {
                    return rate;
                }

                // Si el provider/snapshot devuelve un subset que no contiene `to`, intentamos rebasar
                // a través de EUR (la base canónica que siempre publica Frankfurter completa).
                if (!string.Equals(from, "EUR", StringComparison.OrdinalIgnoreCase))
                {
                    var eurRates = await GetAllRatesInternalAsync("EUR", CancellationToken.None).ConfigureAwait(false);
                    if (eurRates.TryGetValue(from, out var eurToFrom) && eurToFrom > 0m
                        && eurRates.TryGetValue(to, out var eurToTo))
                    {
                        // 1 from = (1/eurToFrom) EUR; 1 EUR = eurToTo `to` → 1 from = eurToTo/eurToFrom `to`.
                        return decimal.Round(eurToTo / eurToFrom, 6, MidpointRounding.ToEven);
                    }
                }

                _logger.LogWarning(
                    "ExchangeRateService.GetRateAsync: par {From}->{To} no encontrado en snapshot. Degradando a 1.0.",
                    from, to);
                return 1.0m;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ExchangeRateService.GetRateAsync: fallo resolviendo {From}->{To}. Degradando a 1.0.",
                    from, to);
                return 1.0m;
            }
        }

        // -------------------------------------------------------------------------
        // GetAllRatesAsync (IReadOnlyDictionary, con CT) — usado por consumidores típicos.
        // -------------------------------------------------------------------------
        public Task<IReadOnlyDictionary<string, decimal>> GetAllRatesAsync(
            string baseCurrency,
            CancellationToken ct = default)
        {
            return GetAllRatesInternalAsync(Normalize(baseCurrency), ct);
        }

        // -------------------------------------------------------------------------
        // GetAllRatesAsync (Dictionary mutable, sin CT, base por defecto "EUR")
        // Devuelve una COPIA mutable para que el caller pueda modificarla sin pisar la cache.
        // -------------------------------------------------------------------------
        public async Task<Dictionary<string, decimal>> GetAllRatesAsync(string baseCurrency = "EUR")
        {
            var snapshot = await GetAllRatesInternalAsync(Normalize(baseCurrency), CancellationToken.None)
                .ConfigureAwait(false);
            return new Dictionary<string, decimal>(snapshot, StringComparer.OrdinalIgnoreCase);
        }

        // -------------------------------------------------------------------------
        // RefreshRatesAsync — punto de entrada del job Hangfire diario.
        // Fetch fresco para EUR, persiste snapshot en BD y refresca la cache.
        // -------------------------------------------------------------------------
        // 🛡️ DisableConcurrentExecution: en HPA multi-replica Render, dos workers pueden disparar
        // el mismo recurring job en la misma ventana; el lock pesimista de Hangfire garantiza
        // un único fetch real contra Frankfurter por ciclo. Timeout 120s (la API responde en <2s).
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 120)]
        // AutomaticRetry=0: no reintentar — el siguiente ciclo (24h después) lo arregla. Reintentar
        // contra un provider caído solo añade ruido al digest y no soluciona nada.
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task RefreshRatesAsync()
        {
            const string baseCurrency = "EUR";
            try
            {
                var fresh = await FetchFromUpstreamAsync(baseCurrency, CancellationToken.None).ConfigureAwait(false);

                // Persistir snapshot ANTES de actualizar la cache. Si la BD falla pero la red iba bien,
                // queremos que el cold-start futuro lo encuentre; si solo refrescamos cache y reiniciamos
                // antes del próximo ciclo, perdemos el dato.
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                await PersistSnapshotAsync(baseCurrency, fresh, ProviderId, today, CancellationToken.None)
                    .ConfigureAwait(false);

                _cache.Set(CacheKey(baseCurrency), (IReadOnlyDictionary<string, decimal>)fresh, CacheTtl);
                _logger.LogInformation(
                    "ExchangeRateService.RefreshRatesAsync: snapshot persistido y cache refrescada con {Count} tasas (base={Base}, source={Provider}).",
                    fresh.Count, baseCurrency, ProviderId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ExchangeRateService.RefreshRatesAsync: fallo refrescando tasas. La cache/snapshot previo (si existían) siguen válidos.");
                // No relanzamos: el job Hangfire no debe marcarse como failed por una caída
                // puntual del upstream. El digest de Hangfire avisará si se cae repetidamente.
            }
        }

        // =========================================================================
        // Internals
        // =========================================================================

        private async Task<IReadOnlyDictionary<string, decimal>> GetAllRatesInternalAsync(
            string normalizedBase,
            CancellationToken ct)
        {
            var key = CacheKey(normalizedBase);

            // 1) Cache caliente — escenario habitual durante las 24h tras el job de refresco.
            if (_cache.TryGetValue<IReadOnlyDictionary<string, decimal>>(key, out var cached) && cached != null)
            {
                return cached;
            }

            // 2) Cold-start desde BD (cualquier reinicio del proceso pierde la cache). Si hay un
            //    snapshot persistido lo hidratamos sin tocar Frankfurter — más rápido y respeta la
            //    cuota del proveedor incluso si el servidor reinicia varias veces al día.
            var snapshotRates = await TryLoadLatestRatesAsync(normalizedBase, ct).ConfigureAwait(false);
            if (snapshotRates != null && snapshotRates.Count > 0)
            {
                _cache.Set(key, snapshotRates, CacheTtl);
                return snapshotRates;
            }

            // 3) Sin cache ni snapshot — primer arranque antes de que el job haya corrido. Fetch on-demand.
            //    Esta rama es la que más se acerca a "convertir endpoint anónimo en proxy del upstream",
            //    así que cacheamos siempre el resultado (live o fallback) para limitar la presión.
            try
            {
                var fresh = await FetchFromUpstreamAsync(normalizedBase, ct).ConfigureAwait(false);
                // Persistir snapshot oportunístico: así el siguiente cold-start no llamará al provider.
                await PersistSnapshotAsync(normalizedBase, fresh, ProviderId, DateOnly.FromDateTime(DateTime.UtcNow), ct)
                    .ConfigureAwait(false);
                _cache.Set(key, (IReadOnlyDictionary<string, decimal>)fresh, CacheTtl);
                return fresh;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ExchangeRateService: upstream fetch falló para base {Base}. Sirviendo fallback estático.",
                    normalizedBase);
                var fallback = RebaseFromEur(normalizedBase);
                // TTL corto para reintentar pronto cuando upstream se recupere.
                _cache.Set(key, fallback, FallbackCacheTtl);
                return fallback;
            }
        }

        private async Task<Dictionary<string, decimal>> FetchFromUpstreamAsync(string normalizedBase, CancellationToken ct)
        {
            // Preferimos el HttpClient nombrado "frankfurter" (BaseAddress + Timeout configurados en
            // Program.cs). Si no está registrado (p.ej. tests unitarios que inyectan un factory ad-hoc),
            // caemos a un cliente genérico con URL absoluta para mantener el método autocontenido.
            var client = _httpClientFactory.CreateClient(FrankfurterClientName);
            HttpResponseMessage response;

            if (client.BaseAddress != null)
            {
                response = await client.GetAsync(string.Format(FrankfurterLatestPath, normalizedBase), ct)
                    .ConfigureAwait(false);
            }
            else
            {
                client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                response = await client.GetAsync(string.Format(FrankfurterAbsoluteUrlTemplate, normalizedBase), ct)
                    .ConfigureAwait(false);
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

                if (!doc.RootElement.TryGetProperty("rates", out var ratesElement) ||
                    ratesElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("Upstream payload missing 'rates' object");
                }

                var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
                {
                    [normalizedBase] = 1.00m
                };

                foreach (var property in ratesElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Number &&
                        property.Value.TryGetDecimal(out var rate))
                    {
                        rates[property.Name.ToUpperInvariant()] = rate;
                    }
                }
                return rates;
            }
        }

        /// <summary>
        /// Lee el snapshot más reciente para una moneda base. Devuelve null si no hay ninguno o
        /// si la BD no está accesible — el caller cae a la siguiente capa (live fetch o fallback).
        /// </summary>
        private async Task<IReadOnlyDictionary<string, decimal>?> TryLoadLatestRatesAsync(string baseCurrency, CancellationToken ct)
        {
            try
            {
                var snapshot = await _context.ExchangeRateSnapshots
                    .AsNoTracking()
                    .Where(s => s.BaseCurrency == baseCurrency)
                    .OrderByDescending(s => s.FetchedAt)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);

                return snapshot == null ? null : DeserializeRates(baseCurrency, snapshot.RatesJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ExchangeRateService: failed to read snapshot from DB for base {Base}.", baseCurrency);
                return null;
            }
        }

        /// <summary>
        /// Lee el snapshot cuya RateDate coincide con <paramref name="asOf"/> (fecha UTC). Si hay
        /// varios snapshots ese día (el job se re-disparó), elige el más reciente por FetchedAt.
        /// </summary>
        private async Task<IReadOnlyDictionary<string, decimal>?> TryLoadHistoricalRatesAsync(string baseCurrency, DateTime asOf)
        {
            try
            {
                var date = DateOnly.FromDateTime(asOf);
                var snapshot = await _context.ExchangeRateSnapshots
                    .AsNoTracking()
                    .Where(s => s.BaseCurrency == baseCurrency && s.RateDate == date)
                    .OrderByDescending(s => s.FetchedAt)
                    .FirstOrDefaultAsync()
                    .ConfigureAwait(false);

                return snapshot == null ? null : DeserializeRates(baseCurrency, snapshot.RatesJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ExchangeRateService: failed historical lookup for {Base} @ {AsOf}.", baseCurrency, asOf);
                return null;
            }
        }

        private static IReadOnlyDictionary<string, decimal>? DeserializeRates(string baseCurrency, string ratesJson)
        {
            if (string.IsNullOrWhiteSpace(ratesJson))
            {
                return null;
            }

            var parsed = JsonSerializer.Deserialize<Dictionary<string, decimal>>(ratesJson);
            if (parsed == null)
            {
                return null;
            }

            // Re-normaliza claves a mayúsculas + asegura que la base aparezca como 1.0
            // (defensivo por si un snapshot antiguo se persistió sin ella).
            var result = new Dictionary<string, decimal>(parsed.Count + 1, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in parsed)
            {
                result[k.ToUpperInvariant()] = v;
            }
            result[baseCurrency] = 1.00m;
            return result;
        }

        private async Task PersistSnapshotAsync(
            string baseCurrency,
            IReadOnlyDictionary<string, decimal> rates,
            string source,
            DateOnly rateDate,
            CancellationToken ct)
        {
            try
            {
                var snapshot = new ExchangeRateSnapshot
                {
                    BaseCurrency = baseCurrency,
                    RatesJson = JsonSerializer.Serialize(rates),
                    Source = source,
                    RateDate = rateDate,
                    FetchedAt = DateTime.UtcNow
                };
                _context.ExchangeRateSnapshots.Add(snapshot);
                await _context.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Persistir snapshot es nice-to-have, no debe abortar el read path ni el job.
                _logger.LogWarning(ex,
                    "ExchangeRateService: failed to persist snapshot for base {Base}.", baseCurrency);
            }
        }

        private static IReadOnlyDictionary<string, decimal> RebaseFromEur(string targetBase)
        {
            if (string.Equals(targetBase, "EUR", StringComparison.OrdinalIgnoreCase))
            {
                return StaticFallbackEurBase;
            }

            if (!StaticFallbackEurBase.TryGetValue(targetBase, out var targetPerEur) || targetPerEur == 0m)
            {
                // Base desconocida → devolver el mapa EUR tal cual; el caller verá
                // que la propia base no figura como 1.0 y puede decidir qué hacer.
                return StaticFallbackEurBase;
            }

            var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var (code, perEur) in StaticFallbackEurBase)
            {
                // Si 1 EUR = perEur en `code`, y 1 EUR = targetPerEur en `targetBase`,
                // entonces 1 targetBase = perEur / targetPerEur en `code`.
                result[code] = decimal.Round(perEur / targetPerEur, 6, MidpointRounding.AwayFromZero);
            }
            result[targetBase] = 1.00m;
            return result;
        }

        private static string Normalize(string? code) =>
            string.IsNullOrWhiteSpace(code) ? "EUR" : code.Trim().ToUpperInvariant();

        private static string CacheKey(string normalizedBase) => $"fxrates:{normalizedBase}";
    }
}
