using Microsoft.EntityFrameworkCore;
using newApi.Common;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using Stripe;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace newApi.Services
{
    /// <summary>
    /// 🛡️ Round 28 MUD-BB: agregador anual DAC7 + exportador XML modelo 238.
    ///
    /// Flujo:
    /// 1. Hangfire RecurringJob @20-enero 02:00 UTC dispara GenerateForYearAsync(prevYear).
    ///    Esto crea/actualiza una fila Dac7SellerSnapshots por cada experto activo durante el año.
    /// 2. Admin invoca endpoint /api/admin/dac7/export/{year}.cs que llama ExportXmlForYearAsync(year).
    /// 3. El XML resultante se sube manualmente a AEAT (no hay API de envío directo).
    ///
    /// Umbral DAC7: incluir en XML solo sellers con TotalGrossSales >= €2.000 OR
    /// TotalTransactionCount >= 30.
    ///
    /// IMPORTANTE: el schema oficial AEAT modelo 238 cambia anualmente (XSD descargable
    /// desde sede.agenciatributaria.gob.es). El XML que generamos sigue la estructura
    /// del OECD Model Reporting Rules (base de DAC7) pero el admin DEBE validar contra
    /// el XSD vigente antes de subir.
    /// </summary>
    // Projection tipada para LINQ — evita dynamic y las inferencias rotas.
    internal record HireRow(int HireId, int ExpertProfileId, DateTime CreatedAt, decimal Amount, string Currency);

    public class Dac7AnnualReportService
    {
        private readonly AppDbContext _context;
        private readonly ILoggingService _loggingService;

        public Dac7AnnualReportService(AppDbContext context, ILoggingService loggingService)
        {
            _context = context;
            _loggingService = loggingService;
        }

        /// <summary>
        /// Genera/actualiza Dac7SellerSnapshots para el año natural completo.
        /// Idempotente: si ya existe la fila (ExpertProfileId, Year) la actualiza.
        /// </summary>
        // 🛡️ Round 28 MUD-CG: lock global por año. Sin esto, en HPA multi-réplica Render
        // 2+ workers pueden ejecutar el job simultáneamente, ambos hacen Add/Update sobre
        // (ExpertProfileId, Year) → DbUpdateConcurrencyException o violación del unique
        // IX_Dac7SellerSnapshot_Expert_Year_uq → snapshot anual CORRUPTO. Como el job solo
        // corre 1 vez al año, el admin se entera 11 días después al revisar el XML.
        // Timeout 1h cubre el peor caso (miles de expertos × FX conversions).
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 3600)]
        public async Task<int> GenerateForYearAsync(int year, CancellationToken ct = default)
        {
            var startOfYear = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var startOfNextYear = startOfYear.AddYears(1);

            // 1) Identificar todos los ExpertProfiles que tuvieron al menos un hire finalizado
            //    durante el año (no filtramos por umbral aquí — el XML aplica el threshold al exportar).
            var rawHires = await _context.SearchHires
                .AsNoTracking()
                .Include(sh => sh.Status)
                .Include(sh => sh.SearchService)
                .Where(sh => sh.CreatedAt >= startOfYear
                          && sh.CreatedAt < startOfNextYear
                          && sh.Status != null
                          && sh.Status.IsFinalizationStatus
                          && sh.SearchService != null
                          && sh.SearchService.ExpertProfileId != null)
                .Select(sh => new HireRow(
                    sh.Id,
                    sh.SearchService!.ExpertProfileId!.Value,
                    sh.CreatedAt,
                    sh.Amount,
                    sh.Currency))
                .ToListAsync(ct);
            var expertsWithHires = rawHires;

            var expertIds = expertsWithHires.Select(e => e.ExpertProfileId).Distinct().ToList();
            if (expertIds.Count == 0)
            {
                return 0;
            }

            // 2) Cargar platform fees del año (FinancialTransaction TransactionType="PlatformFee").
            //    El hire es el RelatedEntityId. Map por hire para sumar al trimestre correcto.
            var hireIds = expertsWithHires.Select(e => e.HireId).ToList();
            var feesByHire = await _context.FinancialTransactions
                .AsNoTracking()
                .Where(ft => ft.TransactionType == "PlatformFee"
                          && ft.RelatedEntityType == "SearchHire"
                          && ft.RelatedEntityId != null
                          && hireIds.Contains(ft.RelatedEntityId.Value))
                .GroupBy(ft => ft.RelatedEntityId!.Value)
                .Select(g => new { HireId = g.Key, Total = g.Sum(ft => Math.Abs(ft.Amount)) })
                .ToDictionaryAsync(x => x.HireId, x => x.Total, ct);

            // 3) Cargar snapshots existentes para idempotencia.
            var existingSnapshots = await _context.Dac7SellerSnapshots
                .Where(s => s.ExpertProfileId != null && expertIds.Contains(s.ExpertProfileId.Value) && s.Year == year)
                .ToListAsync(ct);
            // ExpertProfileId ahora es nullable (FK SET NULL al borrar al experto). El filtro de arriba
            // ya descarta los NULL (snapshots de expertos borrados), así que .Value es seguro aquí.
            var existingByExpert = existingSnapshots.ToDictionary(s => s.ExpertProfileId!.Value);

            // 4) Cargar ExpertProfiles + Users para snapshot de datos legales.
            var profiles = await _context.ExpertProfiles
                .AsNoTracking()
                .IgnoreQueryFilters() // 🛡️ incluir GDPR-deleted para preservar histórico legal
                .Include(p => p.User)
                .Where(p => expertIds.Contains(p.Id))
                .ToListAsync(ct);
            var profileById = profiles.ToDictionary(p => p.Id);

            int generated = 0;
            foreach (var expertProfileId in expertIds)
            {
                var hiresOfExpert = expertsWithHires.Where(e => e.ExpertProfileId == expertProfileId).ToList();

                // Agregar por trimestre
                var aggQ1 = AggregateQuarter(hiresOfExpert, year, 1, feesByHire);
                var aggQ2 = AggregateQuarter(hiresOfExpert, year, 2, feesByHire);
                var aggQ3 = AggregateQuarter(hiresOfExpert, year, 3, feesByHire);
                var aggQ4 = AggregateQuarter(hiresOfExpert, year, 4, feesByHire);

                var profile = profileById.GetValueOrDefault(expertProfileId);
                var user = profile?.User;

                // Datos legales: Users.TaxId/FiscalCountry (MUD-AO ya los persiste para clientes;
                // para EXPERTOS el TIN viene de Stripe Connect — falta wiring desde account.updated
                // que se hará en una fase futura. Por ahora best-effort desde lo que tengamos).
                var snapshot = existingByExpert.GetValueOrDefault(expertProfileId);
                bool isNew = snapshot == null;
                if (isNew)
                {
                    snapshot = new Dac7SellerSnapshot
                    {
                        ExpertProfileId = expertProfileId,
                        Year = year,
                    };
                }

                // 🛡️ Round 28 MUD-BS (follow-up MUD-BH/BF): NO destruir datos que el webhook
                // Dac7DataSyncService ha ido sincronizando durante el año. ANTES estas
                // asignaciones eran '=' incondicionales → el job anual @20-enero nulleaba
                // DOB/Address/IbanLast4 que el webhook account.updated había acumulado todo
                // el año → modelo 238 a AEAT con campos vacíos → multa €300/seller.
                // Ahora usamos '??=' (asignar SOLO si null): el job solo PUEDE rellenar
                // campos faltantes, NUNCA sobreescribir lo que el webhook ya capturó. Si el
                // snapshot del año tiene datos legales completos vía webhook, queda intacto.
                snapshot!.LegalFirstName ??= user?.Name;
                // LegalLastName/DateOfBirth/AddressLine/AddressCity/AddressPostalCode/
                // VatNumber/IbanLast4: SOLO los pone Dac7DataSyncService desde Stripe.account
                // (más fiable que Users). El job anual ya no los toca.
                snapshot.TinCountry ??= user?.TaxIdCountry;
                snapshot.TinValue ??= user?.TaxId;
                snapshot.FiscalCountry ??= user?.FiscalCountry ?? profile?.Country;

                snapshot.GrossSalesQ1 = aggQ1.Gross;
                snapshot.GrossSalesQ2 = aggQ2.Gross;
                snapshot.GrossSalesQ3 = aggQ3.Gross;
                snapshot.GrossSalesQ4 = aggQ4.Gross;

                snapshot.PlatformFeesQ1 = aggQ1.Fees;
                snapshot.PlatformFeesQ2 = aggQ2.Fees;
                snapshot.PlatformFeesQ3 = aggQ3.Fees;
                snapshot.PlatformFeesQ4 = aggQ4.Fees;

                snapshot.TransactionCountQ1 = aggQ1.Count;
                snapshot.TransactionCountQ2 = aggQ2.Count;
                snapshot.TransactionCountQ3 = aggQ3.Count;
                snapshot.TransactionCountQ4 = aggQ4.Count;

                snapshot.SnapshotCreatedAt = DateTime.UtcNow;

                if (isNew)
                {
                    _context.Dac7SellerSnapshots.Add(snapshot);
                }
                generated++;
            }

            await _context.SaveChangesAsync(ct);

            await _loggingService.LogInfoAsync(
                message: $"MUD-BB: DAC7 annual snapshot generated for year {year}",
                details: $"Generated/updated {generated} snapshots for year {year}. Threshold-crossing sellers visible via /api/admin/dac7/export/{year}. ACCIÓN ADMIN: revisar manualmente Dac7SellerSnapshots, completar campos TIN/DOB/IBAN/Address desde Stripe Dashboard si faltan, y subir XML a AEAT antes del 31-enero-{year + 1}.",
                source: "Dac7AnnualReportService.GenerateForYearAsync",
                relatedEntityType: "Dac7",
                additionalData: new { Year = year, Generated = generated });

            return generated;
        }

        private static (decimal Gross, decimal Fees, int Count) AggregateQuarter(
            IEnumerable<HireRow> hires, int year, int quarter, Dictionary<int, decimal> feesByHire)
        {
            var qStart = new DateTime(year, ((quarter - 1) * 3) + 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var qEnd = qStart.AddMonths(3);
            decimal gross = 0m, fees = 0m;
            int count = 0;
            foreach (var h in hires)
            {
                if (h.CreatedAt < qStart || h.CreatedAt >= qEnd) continue;
                gross += h.Amount;
                if (feesByHire.TryGetValue(h.HireId, out var fee))
                {
                    fees += fee;
                }
                count++;
            }
            return (gross, fees, count);
        }

        /// <summary>
        /// Genera el XML modelo 238 para el año. Solo incluye sellers que cruzaron umbral.
        /// El admin descarga este XML y lo sube manualmente a AEAT.
        /// </summary>
        public async Task<string> ExportXmlForYearAsync(int year, CancellationToken ct = default)
        {
            var snapshots = await _context.Dac7SellerSnapshots
                .AsNoTracking()
                .Where(s => s.Year == year)
                .Include(s => s.ExpertProfile)
                    .ThenInclude(p => p!.User)
                .ToListAsync(ct);

            var reportable = snapshots.Where(s => s.CrossedReportingThreshold).ToList();

            XNamespace ns = "https://www.agenciatributaria.gob.es/dac7/modelo238";
            var root = new XElement(ns + "Modelo238",
                new XAttribute("FiscalYear", year),
                new XAttribute("PlatformOperatorName", "Inspecciono"),
                new XAttribute("PlatformOperatorCountry", "ES"),
                new XAttribute("GeneratedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                new XAttribute("ReportableSellersCount", reportable.Count));

            foreach (var s in reportable)
            {
                var sellerXml = new XElement(ns + "Seller",
                    // ExpertProfileId nullable (SET NULL al borrar experto): si está desvinculado,
                    // escribimos vacío en lugar de petar XAttribute con null.
                    new XAttribute("ExpertProfileId", (object?)s.ExpertProfileId ?? string.Empty),
                    new XElement(ns + "Identification",
                        new XElement(ns + "LegalFirstName", s.LegalFirstName ?? string.Empty),
                        new XElement(ns + "LegalLastName", s.LegalLastName ?? string.Empty),
                        new XElement(ns + "DateOfBirth",
                            s.DateOfBirth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty),
                        new XElement(ns + "TIN",
                            new XAttribute("Country", s.TinCountry ?? string.Empty),
                            s.TinValue ?? string.Empty),
                        new XElement(ns + "FiscalCountry", s.FiscalCountry ?? string.Empty),
                        new XElement(ns + "VatNumber", s.VatNumber ?? string.Empty)),
                    new XElement(ns + "Address",
                        new XElement(ns + "Line", s.AddressLine ?? string.Empty),
                        new XElement(ns + "City", s.AddressCity ?? string.Empty),
                        new XElement(ns + "PostalCode", s.AddressPostalCode ?? string.Empty),
                        new XElement(ns + "Country", s.FiscalCountry ?? string.Empty)),
                    new XElement(ns + "FinancialAccount",
                        new XAttribute("Type", "IBAN"),
                        new XElement(ns + "Last4", s.IbanLast4 ?? string.Empty)),
                    new XElement(ns + "Activity",
                        new XAttribute("ReportingCurrency", s.ReportingCurrency),
                        QuarterXml(ns, "Q1", s.GrossSalesQ1, s.PlatformFeesQ1, s.TransactionCountQ1),
                        QuarterXml(ns, "Q2", s.GrossSalesQ2, s.PlatformFeesQ2, s.TransactionCountQ2),
                        QuarterXml(ns, "Q3", s.GrossSalesQ3, s.PlatformFeesQ3, s.TransactionCountQ3),
                        QuarterXml(ns, "Q4", s.GrossSalesQ4, s.PlatformFeesQ4, s.TransactionCountQ4),
                        new XElement(ns + "AnnualTotal",
                            new XElement(ns + "GrossSales", s.TotalGrossSales.ToString("F2", CultureInfo.InvariantCulture)),
                            new XElement(ns + "PlatformFees", s.TotalPlatformFees.ToString("F2", CultureInfo.InvariantCulture)),
                            new XElement(ns + "TransactionCount", s.TotalTransactionCount))));
                root.Add(sellerXml);
            }

            var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
            using var ms = new MemoryStream();
            using (var writer = XmlWriter.Create(ms, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
            {
                doc.Save(writer);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static XElement QuarterXml(XNamespace ns, string name, decimal gross, decimal fees, int count)
        {
            return new XElement(ns + "Quarter",
                new XAttribute("Name", name),
                new XElement(ns + "GrossSales", gross.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement(ns + "PlatformFees", fees.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement(ns + "TransactionCount", count));
        }

        /// <summary>
        /// Cuenta sellers que cruzarían el threshold sin todavía generar XML.
        /// Útil para dashboard admin: "tienes 7 expertos sujetos a DAC7 este año".
        /// </summary>
        public async Task<int> CountReportableForYearAsync(int year, CancellationToken ct = default)
        {
            return await _context.Dac7SellerSnapshots
                .AsNoTracking()
                .Where(s => s.Year == year)
                .CountAsync(s => (s.GrossSalesQ1 + s.GrossSalesQ2 + s.GrossSalesQ3 + s.GrossSalesQ4) >= 2000m
                              || (s.TransactionCountQ1 + s.TransactionCountQ2 + s.TransactionCountQ3 + s.TransactionCountQ4) >= 30, ct);
        }
    }
}
