using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using newApi.Configuration;
using newApi.DataLayer.Models;

namespace newApi.Services
{
    /// <summary>
    /// 🛡️ Round 28 — Sprint 2 (S2-P0-11): tracking del umbral OSS €10.000/año UE.
    ///
    /// CONTEXTO REGULATORIO
    /// --------------------
    /// Directiva 2017/2455 art. 59c obliga a registrar al vendedor en One-Stop Shop (modelo 369
    /// en AEAT) cuando las ventas B2C transfronterizas dentro de la UE superen €10.000 anuales
    /// AGREGADAS (no por país). Sin OSS, cada país UE reclama su IVA local cuando los intercambios
    /// AEAT↔VAT detectan el incumplimiento → multa típica: 100% IVA no recaudado + recargo + intereses.
    ///
    /// IMPLEMENTACIÓN
    /// --------------
    /// Antes existía un TODO sin lógica (PlatformFiscalProfile.cs líneas 50-63). Ahora este
    /// servicio calcula el YTD on-demand desde SearchHires sin tabla cacheada — para 10k hires/año
    /// la query es O(N) y se ejecuta en &lt;100 ms. Si el volumen escala a 100k+, migrar a una tabla
    /// PlatformOssCountryStats con job Hangfire incremental.
    ///
    /// CRITERIOS DE INCLUSIÓN
    /// ----------------------
    /// Una hire cuenta como "B2C UE cross-border" si:
    ///  - Cliente identificado (ClientId != null) sin VAT number (no es B2B con reverse-charge).
    ///  - SearchHire.ClientVatCountryCode != país de la plataforma (FiscalAddress.Country, default ES).
    ///  - Ambos países en EEA UE (excluye GB post-Brexit, CH, NO).
    ///  - Estado de la hire es finalizada (no canceladas/refundadas — se usa Amount real).
    ///
    /// USO
    /// ---
    /// Admin: GET /api/admin/oss-stats → JSON con YTD por país + umbral + alerta.
    /// Programa: Program.cs llamará a ValidateAtStartupAsync() para fail-fast si IsVatRegistered=true
    /// pero el umbral ya está cruzado sin OSS registrado.
    /// </summary>
    public class OssThresholdService
    {
        private readonly AppDbContext _context;
        private readonly PlatformFiscalProfile _fiscal;
        private readonly ILoggingService _loggingService;
        private readonly ILogger<OssThresholdService> _logger;

        public OssThresholdService(
            AppDbContext context,
            IOptions<PlatformFiscalProfile> fiscal,
            ILoggingService loggingService,
            ILogger<OssThresholdService> logger)
        {
            _context = context;
            _fiscal = fiscal.Value;
            _loggingService = loggingService;
            _logger = logger;
        }

        /// <summary>
        /// Resultado del análisis del umbral OSS para el año actual.
        /// </summary>
        public class OssThresholdReport
        {
            public int Year { get; set; }
            public string PlatformCountry { get; set; } = string.Empty;
            public decimal ThresholdEur { get; set; }
            public int WarningPercent { get; set; }
            public decimal TotalCrossBorderB2cEur { get; set; }
            public decimal RemainingToThresholdEur { get; set; }
            public decimal PercentOfThreshold { get; set; }
            public bool ThresholdExceeded { get; set; }
            public bool WarningTriggered { get; set; }
            public List<PerCountryStats> PerCountry { get; set; } = new List<PerCountryStats>();
            public bool OssRegistered { get; set; }
            public bool IsVatRegistered { get; set; }
            public string ComplianceStatus { get; set; } = string.Empty;
        }

        public class PerCountryStats
        {
            public string CountryCode { get; set; } = string.Empty;
            public decimal TotalB2cEur { get; set; }
            public int HireCount { get; set; }
        }

        /// <summary>
        /// 🛡️ Países UE EEA (excluye UK post-Brexit, CH, NO). OSS aplica solo a estos.
        /// Mantener sincronizado con la lista de la UE — si entra/sale un país, actualizar aquí.
        /// </summary>
        private static readonly HashSet<string> EuEeaCountries = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR",
            "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK",
            "SI", "ES", "SE"
        };

        /// <summary>
        /// Calcula el snapshot del año actual: ventas cross-border B2C UE acumuladas + alerta.
        /// </summary>
        public async Task<OssThresholdReport> GetCurrentYearReportAsync(CancellationToken ct = default)
        {
            var year = System.DateTime.UtcNow.Year;
            var platformCountry = (_fiscal.FiscalAddress?.Country ?? "ES").Trim().ToUpperInvariant();

            // Filtrar hires del año actual cuyo cliente está en otro país UE.
            // Excluir hires canceladas/refundadas (Status no finalizado o status que indica refund).
            var startOfYear = new System.DateTime(year, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
            var endOfYear = startOfYear.AddYears(1);

            var hires = await _context.SearchHires
                .AsNoTracking()
                .Where(h => h.CreatedAt >= startOfYear && h.CreatedAt < endOfYear)
                .Where(h => h.ClientId != null) // solo clientes identificados
                .Where(h => string.IsNullOrEmpty(h.ClientVatNumber)) // B2C: sin NIF B2B
                .Where(h => !string.IsNullOrEmpty(h.ClientVatCountryCode))
                .Where(h => h.ClientVatCountryCode != platformCountry)
                // 🛡️ Captura diferida (modo vendedor): un hire con CaptureStatus="Authorized" tiene el
                // PI sólo AUTORIZADO (requires_capture), NO cobrado, hasta 48h hasta que el vendedor
                // confirma la cita. "Failed" = captura cancelada/fallida → tampoco se cobró. Contar ese
                // dinero aún-no-recaudado podía disparar el umbral OSS €10.000 de forma prematura.
                // Excluimos AMBOS estados de no-cobrado. CRÍTICO null-safe: los hires legacy (y los
                // recién creados) tienen CaptureStatus=null AUNQUE SÍ se cobraron — NO deben excluirse,
                // si no INFRA-reportaríamos el umbral (riesgo fiscal peor que el original). "Pending"
                // (captura en vuelo que normalmente acaba "Captured") y "Captured" siguen contando.
                .Where(h => h.CaptureStatus == null
                            || (h.CaptureStatus != "Authorized" && h.CaptureStatus != "Failed"))
                .Select(h => new
                {
                    h.ClientVatCountryCode,
                    Amount = h.BaseAmount ?? h.Amount,
                })
                .ToListAsync(ct);

            // Filtrar solo países UE EEA (excluye UK/CH/NO/US/etc., que no son OSS).
            var ueHires = hires
                .Where(h => !string.IsNullOrEmpty(h.ClientVatCountryCode)
                            && EuEeaCountries.Contains(h.ClientVatCountryCode!.Trim().ToUpperInvariant()))
                .ToList();

            var perCountry = ueHires
                .GroupBy(h => h.ClientVatCountryCode!.Trim().ToUpperInvariant())
                .Select(g => new PerCountryStats
                {
                    CountryCode = g.Key,
                    TotalB2cEur = g.Sum(x => x.Amount),
                    HireCount = g.Count()
                })
                .OrderByDescending(s => s.TotalB2cEur)
                .ToList();

            var total = perCountry.Sum(s => s.TotalB2cEur);
            var percent = _fiscal.OssThresholdEur > 0
                ? (total / _fiscal.OssThresholdEur) * 100m
                : 0m;
            var exceeded = total >= _fiscal.OssThresholdEur;
            var warning = percent >= _fiscal.OssThresholdWarningPercent;

            // Determinar estado de compliance.
            string complianceStatus;
            if (!_fiscal.IsVatRegistered)
            {
                complianceStatus = "PRE_FISCAL_REGISTRATION"; // Antes del alta — no aplica IVA aún, pero el tracking sigue activo para el día del flip.
            }
            else if (exceeded && !_fiscal.OssRegistered)
            {
                complianceStatus = "VIOLATION_THRESHOLD_EXCEEDED_NO_OSS"; // CRÍTICO: cruzó umbral sin OSS.
            }
            else if (warning && !_fiscal.OssRegistered)
            {
                complianceStatus = "WARNING_APPROACHING_THRESHOLD"; // 80%+: iniciar trámite AEAT modelo 369.
            }
            else if (exceeded && _fiscal.OssRegistered)
            {
                complianceStatus = "OK_REGISTERED_AND_REPORTING"; // OSS activo y operando correctamente.
            }
            else
            {
                complianceStatus = "OK_UNDER_THRESHOLD";
            }

            return new OssThresholdReport
            {
                Year = year,
                PlatformCountry = platformCountry,
                ThresholdEur = _fiscal.OssThresholdEur,
                WarningPercent = _fiscal.OssThresholdWarningPercent,
                TotalCrossBorderB2cEur = total,
                RemainingToThresholdEur = System.Math.Max(0m, _fiscal.OssThresholdEur - total),
                PercentOfThreshold = System.Math.Round(percent, 2),
                ThresholdExceeded = exceeded,
                WarningTriggered = warning,
                PerCountry = perCountry,
                OssRegistered = _fiscal.OssRegistered,
                IsVatRegistered = _fiscal.IsVatRegistered,
                ComplianceStatus = complianceStatus
            };
        }

        /// <summary>
        /// Llamada periódica (admin endpoint o job): emite Critical log + admin notification cuando
        /// el umbral está cruzado (o cerca) y no está OSS registrado. Idempotente — no inunda
        /// notificaciones al admin: el caller debe deduplicar (un email/día por estado).
        /// </summary>
        // 🛡️ Round 28 MUD-CG: lock multi-réplica. Job mensual; con HPA Render 2+ workers
        // podían emitir doble alerta al admin + métricas duplicadas. Timeout 5min suficiente.
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 300)]
        public async Task EmitAlertIfNeededAsync(CancellationToken ct = default)
        {
            var report = await GetCurrentYearReportAsync(ct);
            if (report.ComplianceStatus == "VIOLATION_THRESHOLD_EXCEEDED_NO_OSS")
            {
                await _loggingService.LogCriticalAsync(
                    message: "OSS THRESHOLD VIOLATION: cross-border B2C UE sales exceeded €10.000 without OSS registration",
                    details: $"Year {report.Year}: total B2C UE cross-border = €{report.TotalCrossBorderB2cEur:F2} (threshold €{report.ThresholdEur:F0}). " +
                             $"Países: {string.Join(", ", report.PerCountry.Select(p => $"{p.CountryCode}=€{p.TotalB2cEur:F2}"))}. " +
                             $"ACCIÓN ADMIN URGENTE: dar de alta plataforma en OSS (AEAT modelo 369) y settear PlatformFiscal__OssRegistered=true. " +
                             $"Sin esto, riesgo de multa fiscal en cada país UE.",
                    source: "OssThresholdService.EmitAlertIfNeededAsync",
                    relatedEntityType: "PlatformFiscal",
                    relatedEntityId: 0,
                    additionalData: new
                    {
                        report.Year,
                        report.TotalCrossBorderB2cEur,
                        report.ThresholdEur,
                        report.PerCountry,
                        report.OssRegistered,
                        report.IsVatRegistered,
                        report.ComplianceStatus
                    });
            }
            else if (report.ComplianceStatus == "WARNING_APPROACHING_THRESHOLD")
            {
                await _loggingService.LogCriticalAsync(
                    message: "OSS THRESHOLD WARNING: approaching €10.000 cross-border B2C UE limit",
                    details: $"Year {report.Year}: total €{report.TotalCrossBorderB2cEur:F2} ({report.PercentOfThreshold:F1}% del umbral €{report.ThresholdEur:F0}). " +
                             $"Iniciar trámite AEAT modelo 369 antes de cruzar el límite. Países: {string.Join(", ", report.PerCountry.Select(p => $"{p.CountryCode}=€{p.TotalB2cEur:F2}"))}.",
                    source: "OssThresholdService.EmitAlertIfNeededAsync",
                    relatedEntityType: "PlatformFiscal",
                    relatedEntityId: 0,
                    additionalData: new { report.Year, report.TotalCrossBorderB2cEur, report.PercentOfThreshold, report.PerCountry });
            }
        }

        /// <summary>
        /// Llamada en arranque (Program.cs) — solo loguea, NO bloquea el inicio del servicio para
        /// no romper la app por un fallo fiscal silencioso. El admin debe actuar manualmente al
        /// recibir el Critical log + email.
        /// </summary>
        public async Task ValidateAtStartupAsync(CancellationToken ct = default)
        {
            try
            {
                if (!_fiscal.IsVatRegistered)
                {
                    _logger.LogInformation("OssThresholdService: IsVatRegistered=false, tracking en modo pre-fiscal (pre-flip). No se emite alerta de violación, pero el monitor sigue activo.");
                    return;
                }
                await EmitAlertIfNeededAsync(ct);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "OssThresholdService.ValidateAtStartupAsync falló (best-effort, no bloquea startup)");
            }
        }
    }
}
