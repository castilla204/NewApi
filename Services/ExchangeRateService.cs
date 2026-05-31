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
    /// 🌍 Round 23 — Provider chain:
    ///   a) PRIMARY:  fawazahmed0/currency-api vía jsDelivr (cubre LATAM: ARS, CLP, COP, MXN, BRL…).
    ///   b) FALLBACK CDN: fawazahmed0 vía Cloudflare Pages (mismo dataset, distinta CDN).
    ///   c) FALLBACK ECB: Frankfurter (datos del ECB; NO incluye ARS/CLP/COP — solo G10 + EU).
    ///   d) Último recurso: <see cref="StaticFallbackEurBase"/> hardcodeado.
    ///
    /// Cadena de escritura: job Hangfire diario 06:00 UTC → fetch (chain) → insert snapshot →
    /// set IMemoryCache. Idempotente: dos ejecuciones el mismo día crean dos snapshots (queremos
    /// histórico de cuándo refrescamos), pero la cache queda con la última.
    /// </summary>
    public sealed class ExchangeRateService : IExchangeRateService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly AppDbContext _context;
        private readonly ILogger<ExchangeRateService> _logger;

        // 🌍 Round 23: provider chain. Cada entrada es (clienteHttpFactory, pathRelativo, parserId,
        // sourceLabel persistido en BD). Orden = prioridad descendente; el primero que responda
        // con un payload válido gana. Si todos fallan, el caller cae al static dict.
        //
        // - fawazahmed0 publica EUR.json con claves en minúsculas: {"date":"2026-05-31","eur":{"usd":1.08,"ars":1234.5,...}}.
        //   Las claves bajo "eur" son las divisas TARGET; los valores ya son "1 EUR = X target"
        //   (misma semántica que Frankfurter, no hay que invertir nada).
        // - Frankfurter publica: {"base":"EUR","date":"2026-05-30","rates":{"USD":1.08,...}}.
        //   Claves en mayúsculas dentro de "rates".
        private const string PrimaryClientName     = "fx-primary";       // jsDelivr CDN
        private const string PrimaryPath           = "npm/@fawazahmed0/currency-api@latest/v1/currencies/{0}.json";
        private const string PrimarySourceLabel    = "fawazahmed0";

        private const string FallbackCdnClientName = "fx-fallback-cdn";  // Cloudflare Pages
        private const string FallbackCdnPath       = "v1/currencies/{0}.json";
        private const string FallbackCdnSourceLabel = "fawazahmed0-cdn";

        private const string FallbackEcbClientName = "fx-fallback-ecb";  // Frankfurter (ECB)
        private const string FallbackEcbPath       = "latest?from={0}";
        private const string FallbackEcbSourceLabel = "frankfurter-fallback";
        // URL absoluta de respaldo para tests unitarios que inyectan un factory sin clientes nombrados.
        private const string FallbackEcbAbsoluteUrlTemplate = "https://api.frankfurter.app/latest?from={0}";

        // Source label genérico usado cuando logueamos sin contexto del provider concreto
        // (p.ej. arranque del job antes de saber cuál ganará). Persistimos el real en `PersistSnapshotAsync`.
        private const string DefaultProviderLabel = "fawazahmed0";

        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
        private static readonly TimeSpan FallbackCacheTtl = TimeSpan.FromMinutes(5);

        // 🌍 Round 23: whitelist de las 10 ISOs que el frontend expone (ver CurrenciesController).
        // Filtramos el payload de fawazahmed0 (que trae ~200 monedas) antes de persistir para
        // no llenar la BD con códigos que el selector ni siquiera muestra.
        private static readonly HashSet<string> SupportedCurrenciesList =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "EUR", "USD", "GBP", "MXN", "BRL",
                "ARS", "CLP", "COP", "JPY", "CNY",
                // Extras que el StaticFallback aún tiene y que algún consumidor (RefundService,
                // StripeCurrencyMapping) puede pedir aunque no estén en el selector UI:
                "CHF", "CAD",
            };

        // Fallback estático con orden de magnitud razonable a 2026-05. Solo se usa si
        // todos los providers fallan Y no hay caché previa. Mejor servir tasas "casi correctas"
        // que romper la UI por completo.
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
                // a través de EUR (la base canónica que siempre publica el provider completa).
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
        // un único fetch real contra el provider por ciclo. Timeout 120s (la API responde en <2s).
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 120)]
        // AutomaticRetry=0: no reintentar — el siguiente ciclo (24h después) lo arregla. Reintentar
        // contra un provider caído solo añade ruido al digest y no soluciona nada.
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task RefreshRatesAsync()
        {
            const string baseCurrency = "EUR";
            try
            {
                var (fresh, sourceLabel) = await FetchFromUpstreamAsync(baseCurrency, CancellationToken.None).ConfigureAwait(false);

                // Persistir snapshot ANTES de actualizar la cache. Si la BD falla pero la red iba bien,
                // queremos que el cold-start futuro lo encuentre; si solo refrescamos cache y reiniciamos
                // antes del próximo ciclo, perdemos el dato.
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                await PersistSnapshotAsync(baseCurrency, fresh, sourceLabel, today, CancellationToken.None)
                    .ConfigureAwait(false);

                _cache.Set(CacheKey(baseCurrency), (IReadOnlyDictionary<string, decimal>)fresh, CacheTtl);
                _logger.LogInformation(
                    "ExchangeRateService.RefreshRatesAsync: snapshot persistido y cache refrescada con {Count} tasas (base={Base}, source={Provider}).",
                    fresh.Count, baseCurrency, sourceLabel);
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
            //    snapshot persistido lo hidratamos sin tocar el provider — más rápido y respeta la
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
                var (fresh, sourceLabel) = await FetchFromUpstreamAsync(normalizedBase, ct).ConfigureAwait(false);
                // Persistir snapshot oportunístico: así el siguiente cold-start no llamará al provider.
                await PersistSnapshotAsync(normalizedBase, fresh, sourceLabel, DateOnly.FromDateTime(DateTime.UtcNow), ct)
                    .ConfigureAwait(false);
                _cache.Set(key, (IReadOnlyDictionary<string, decimal>)fresh, CacheTtl);
                return fresh;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ExchangeRateService: provider chain agotada para base {Base}. Sirviendo fallback estático.",
                    normalizedBase);
                var fallback = RebaseFromEur(normalizedBase);
                // TTL corto para reintentar pronto cuando upstream se recupere.
                _cache.Set(key, fallback, FallbackCacheTtl);
                return fallback;
            }
        }

        /// <summary>
        /// 🌍 Round 23 — Provider chain. Intenta en orden: fawazahmed0 (jsDelivr) → fawazahmed0 (Cloudflare)
        /// → Frankfurter (ECB). Devuelve el primer payload válido junto a la etiqueta del provider
        /// que ganó (para persistirla en <c>ExchangeRateSnapshots.Source</c> y diagnóstico).
        /// Lanza si TODOS los providers fallan — el caller hace el último fallback estático.
        /// </summary>
        private async Task<(Dictionary<string, decimal> Rates, string Source)> FetchFromUpstreamAsync(
            string normalizedBase,
            CancellationToken ct)
        {
            // Lista (clientName, pathTemplate, parserKind, sourceLabel, absoluteFallbackUrl?).
            // El parserKind discrimina entre el formato fawazahmed0 ("eur":{"usd":...}) y el de
            // Frankfurter ("rates":{"USD":...}). Mismo método de parsing, distinta extracción.
            var providers = new (string ClientName, string Path, ProviderPayloadFormat Format, string Source, string? AbsoluteFallback)[]
            {
                (PrimaryClientName,     string.Format(PrimaryPath,     normalizedBase.ToLowerInvariant()), ProviderPayloadFormat.Fawazahmed,  PrimarySourceLabel,    null),
                (FallbackCdnClientName, string.Format(FallbackCdnPath, normalizedBase.ToLowerInvariant()), ProviderPayloadFormat.Fawazahmed,  FallbackCdnSourceLabel, null),
                (FallbackEcbClientName, string.Format(FallbackEcbPath, normalizedBase),                    ProviderPayloadFormat.Frankfurter, FallbackEcbSourceLabel, string.Format(FallbackEcbAbsoluteUrlTemplate, normalizedBase)),
            };

            Exception? lastError = null;
            foreach (var p in providers)
            {
                try
                {
                    var rates = await TryFetchOneAsync(p.ClientName, p.Path, p.Format, normalizedBase, p.AbsoluteFallback, ct)
                        .ConfigureAwait(false);

                    // Whitelist defensiva: aunque fawazahmed devuelve ~200 monedas, solo persistimos
                    // las 10 ISOs que el selector UI expone (+ extras necesarias para Stripe Connect).
                    // Evita llenar la BD con un blob JSON gigante de divisas que nadie pide.
                    var filtered = FilterToSupported(rates, normalizedBase);
                    if (filtered.Count > 1) // > 1 para asegurar que tenemos la base + al menos una target.
                    {
                        if (!ReferenceEquals(p, providers[0]))
                        {
                            _logger.LogInformation(
                                "ExchangeRateService: provider primario falló, sirviendo desde {Source} ({Count} tasas).",
                                p.Source, filtered.Count);
                        }
                        return (filtered, p.Source);
                    }

                    _logger.LogWarning(
                        "ExchangeRateService: provider {Source} devolvió un payload válido pero sin tasas soportadas tras whitelist. Probando siguiente.",
                        p.Source);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _logger.LogWarning(ex,
                        "ExchangeRateService: provider {Source} falló para base {Base}. Probando siguiente en la cadena.",
                        p.Source, normalizedBase);
                }
            }

            throw new InvalidOperationException(
                $"ExchangeRateService: todos los providers de la cadena fallaron para base {normalizedBase}.",
                lastError);
        }

        private async Task<Dictionary<string, decimal>> TryFetchOneAsync(
            string clientName,
            string path,
            ProviderPayloadFormat format,
            string normalizedBase,
            string? absoluteFallbackUrl,
            CancellationToken ct)
        {
            var client = _httpClientFactory.CreateClient(clientName);
            HttpResponseMessage response;

            if (client.BaseAddress != null)
            {
                response = await client.GetAsync(path, ct).ConfigureAwait(false);
            }
            else if (!string.IsNullOrEmpty(absoluteFallbackUrl))
            {
                // Tests unitarios o entornos sin el cliente nombrado registrado: usa la URL absoluta
                // del fallback ECB (única para la que mantenemos URL absoluta). Para fawazahmed,
                // exigimos el cliente nombrado — el setup de tests debe inyectarlo si quiere primaria.
                client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                response = await client.GetAsync(absoluteFallbackUrl, ct).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException(
                    $"HttpClient '{clientName}' no registrado y no hay URL absoluta de respaldo.");
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

                return format switch
                {
                    ProviderPayloadFormat.Fawazahmed => ParseFawazahmedPayload(doc.RootElement, normalizedBase),
                    ProviderPayloadFormat.Frankfurter => ParseFrankfurterPayload(doc.RootElement, normalizedBase),
                    _ => throw new InvalidOperationException($"Formato de provider desconocido: {format}")
                };
            }
        }

        /// <summary>
        /// fawazahmed0/currency-api payload: {"date":"2026-05-31","eur":{"usd":1.08,"ars":1234.5,...}}.
        /// La propiedad raíz que contiene las tasas se llama igual que la base (en minúsculas);
        /// los valores ya son "1 base = X target", misma semántica que Frankfurter.
        /// </summary>
        private static Dictionary<string, decimal> ParseFawazahmedPayload(JsonElement root, string normalizedBase)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("fawazahmed payload: root no es un objeto JSON");
            }

            var baseKey = normalizedBase.ToLowerInvariant();
            if (!root.TryGetProperty(baseKey, out var ratesElement) ||
                ratesElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"fawazahmed payload: falta el objeto '{baseKey}' con las tasas");
            }

            return ExtractRates(ratesElement, normalizedBase);
        }

        /// <summary>
        /// Frankfurter payload: {"base":"EUR","date":"2026-05-30","rates":{"USD":1.08,...}}.
        /// Claves dentro de "rates" en MAYÚSCULAS; semántica idéntica a fawazahmed.
        /// </summary>
        private static Dictionary<string, decimal> ParseFrankfurterPayload(JsonElement root, string normalizedBase)
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("rates", out var ratesElement) ||
                ratesElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Frankfurter payload: falta el objeto 'rates'");
            }

            return ExtractRates(ratesElement, normalizedBase);
        }

        /// <summary>
        /// Extrae números de un objeto JSON {clave: número, ...} a Dictionary&lt;string, decimal&gt;,
        /// normalizando claves a MAYÚSCULAS y garantizando que la base aparezca con valor 1.0.
        /// </summary>
        private static Dictionary<string, decimal> ExtractRates(JsonElement ratesElement, string normalizedBase)
        {
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

        /// <summary>
        /// Whitelist defensiva: filtra el dict de tasas a las divisas soportadas por la app.
        /// La base siempre se mantiene (aunque por algún motivo no estuviera en la whitelist,
        /// el caller necesita poder rebasar desde ella).
        /// </summary>
        private static Dictionary<string, decimal> FilterToSupported(
            IDictionary<string, decimal> rates,
            string normalizedBase)
        {
            var filtered = new Dictionary<string, decimal>(SupportedCurrenciesList.Count + 1, StringComparer.OrdinalIgnoreCase);
            foreach (var (code, value) in rates)
            {
                if (SupportedCurrenciesList.Contains(code) && value > 0m)
                {
                    filtered[code.ToUpperInvariant()] = value;
                }
            }
            // Defensivo: garantizamos la base. Si no estaba en la whitelist, la añadimos a 1.0
            // para que GetRateAsync(base, base) y los rebases sigan funcionando.
            filtered[normalizedBase] = 1.00m;
            return filtered;
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

        /// <summary>Discrimina el formato del JSON publicado por cada provider.</summary>
        private enum ProviderPayloadFormat
        {
            /// <summary>fawazahmed0: {"date":"...","eur":{"usd":...}} — clave raíz = base en lowercase.</summary>
            Fawazahmed,
            /// <summary>Frankfurter: {"base":"EUR","date":"...","rates":{"USD":...}} — siempre bajo "rates".</summary>
            Frankfurter,
        }
    }
}
