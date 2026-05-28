using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using newApi.Configuration;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    /// <summary>
    /// Implementación Postgres con UPSERT + SELECT ... FOR UPDATE para serializar incrementos
    /// entre réplicas. SÓLO debe invocarse desde InvoiceService cuando IsVatRegistered=true.
    /// </summary>
    public class InvoiceNumberService : IInvoiceNumberService
    {
        private readonly AppDbContext _context;
        private readonly PlatformFiscalProfile _fiscal;

        public InvoiceNumberService(AppDbContext context, IOptions<PlatformFiscalProfile> fiscal)
        {
            _context = context;
            _fiscal = fiscal?.Value ?? new PlatformFiscalProfile();
        }

        public async Task<string> NextAsync(string seriesPrefix, CancellationToken ct = default)
        {
            // 🔧 FISCAL FLIP: fail-fast si no estamos registrados. Numeración correlativa NO tiene
            // sentido (ni es legal) sin alta fiscal — emitir números formales sin alta es justo lo
            // que queremos evitar pre-flip.
            if (!_fiscal.IsVatRegistered)
            {
                throw new InvalidOperationException(
                    "InvoiceNumberService.NextAsync invocado con PlatformFiscal.IsVatRegistered=false. " +
                    "La numeración correlativa requiere alta fiscal activa. Revisar el flujo en InvoiceService.");
            }
            if (string.IsNullOrWhiteSpace(seriesPrefix))
                throw new ArgumentException("seriesPrefix vacío", nameof(seriesPrefix));

            // Año efectivo: configurable; default = año UTC actual (reset anual estándar).
            var year = _fiscal.InvoiceSeriesYear > 0 ? _fiscal.InvoiceSeriesYear : DateTime.UtcNow.Year;
            var seriesCode = seriesPrefix.Trim();

            // Transacción + lock pesimista para serializar incrementos entre réplicas (Render puede
            // tener N réplicas; sin FOR UPDATE habría race → dos facturas con el mismo número).
            await using var tx = await _context.Database.BeginTransactionAsync(ct);
            try
            {
                // 1) Asegurar fila existente (UPSERT idempotente, no genera huecos si compite).
                await _context.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO ""InvoiceCounters"" (""SeriesCode"", ""Year"", ""NextNumber"", ""CreatedAt"")
                    VALUES ({seriesCode}, {year}, 1, {DateTime.UtcNow})
                    ON CONFLICT (""SeriesCode"", ""Year"") DO NOTHING;
                ", ct);

                // 2) Lock pesimista + leer NextNumber actual.
                var nextNumber = await _context.InvoiceCounters
                    .FromSqlInterpolated($@"
                        SELECT * FROM ""InvoiceCounters""
                        WHERE ""SeriesCode"" = {seriesCode} AND ""Year"" = {year}
                        FOR UPDATE
                    ")
                    .AsNoTracking()
                    .Select(c => c.NextNumber)
                    .FirstAsync(ct);

                // 3) Incrementar + persistir EN LA MISMA TRANSACCIÓN.
                await _context.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE ""InvoiceCounters""
                    SET ""NextNumber"" = ""NextNumber"" + 1, ""UpdatedAt"" = {DateTime.UtcNow}
                    WHERE ""SeriesCode"" = {seriesCode} AND ""Year"" = {year};
                ", ct);

                await tx.CommitAsync(ct);

                // 4) Componer: "INSP-2026-000001". Quita el guion final del prefijo si lo tiene.
                var prefixClean = seriesCode.TrimEnd('-');
                return $"{prefixClean}-{year:D4}-{nextNumber:D6}";
            }
            catch
            {
                try { await tx.RollbackAsync(ct); } catch { /* tx ya muerta */ }
                throw;
            }
        }
    }
}
