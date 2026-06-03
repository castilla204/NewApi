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

            // 🛡️ Round 28 TX-6: envuelto en CreateExecutionStrategy().ExecuteAsync.
            // CRÍTICO LEGAL: sin este wrap, un retry transient durante el FOR UPDATE perdería
            // el lock pesimista entre intentos → dos réplicas podrían emitir facturas con el
            // MISMO número correlativo (violación fiscal española — huecos/duplicados en serie).
            // Con el wrap, el retry completo se reejecuta atómicamente: el INSERT...ON CONFLICT
            // es idempotente, el FOR UPDATE bloquea correctamente en cada intento, y el UPDATE
            // incrementa de forma segura. NUNCA dos retries devolverán el mismo número.
            string invoiceNumber = string.Empty;
            var invoiceStrategy = _context.Database.CreateExecutionStrategy();
            await invoiceStrategy.ExecuteAsync(async () =>
            {
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
                    invoiceNumber = $"{prefixClean}-{year:D4}-{nextNumber:D6}";
                }
                catch
                {
                    try { await tx.RollbackAsync(ct); } catch { /* tx ya muerta */ }
                    throw;
                }
            });
            return invoiceNumber;
        }
    }
}
