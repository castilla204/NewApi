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

                    // ⚠️ AUDITORÍA [M6] Medium (latente): este commit QUEMA el número correlativo de forma irreversible (NextNumber+1) ANTES de que el PDF se envíe con éxito. El llamador (InvoiceService.cs:102-110) solo evita reusar este número vía IMemoryCache in-process.
                    // Disparo/ataque: si el envío posterior falla y Hangfire reintenta tras un reinicio/redeploy o en otra réplica (caché vacía), se vuelve a entrar aquí y se quema OTRO número → el anterior queda como hueco sin documento (incumple RD 1619/2012 art. 6.1.a). Latente hoy (IsVatRegistered=false), se activa con el flip fiscal.
                    // Fix: que el llamador persista el número reservado en SearchHire.InvoiceNumber en esta misma transacción y lo reuse en reintentos, en vez de depender de la caché de proceso.
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

        // Namespace del advisory lock (forma de DOS claves). Ocupa un espacio de locks distinto al de
        // los pg_advisory_xact_lock de UNA clave que usa RefundService sobre el mismo searchHireId, por
        // lo que NUNCA colisiona con ellos. Constante fija arbitraria ("INV").
        private const long InvoiceLockNamespace = 32441038;

        public async Task<string> ReserveForHireAsync(int hireId, string seriesPrefix, CancellationToken ct = default)
        {
            // Mismo fail-fast que NextAsync: numeración correlativa sólo con alta fiscal activa.
            if (!_fiscal.IsVatRegistered)
            {
                throw new InvalidOperationException(
                    "InvoiceNumberService.ReserveForHireAsync invocado con PlatformFiscal.IsVatRegistered=false. " +
                    "La numeración correlativa requiere alta fiscal activa. Revisar el flujo en InvoiceService.");
            }
            if (string.IsNullOrWhiteSpace(seriesPrefix))
                throw new ArgumentException("seriesPrefix vacío", nameof(seriesPrefix));

            var year = _fiscal.InvoiceSeriesYear > 0 ? _fiscal.InvoiceSeriesYear : DateTime.UtcNow.Year;
            var seriesCode = seriesPrefix.Trim();
            var prefixClean = seriesCode.TrimEnd('-');

            string invoiceNumber = string.Empty;
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _context.Database.BeginTransactionAsync(ct);
                try
                {
                    // 1) Serializar por hire. Dos ejecuciones concurrentes del MISMO hire se ordenan
                    //    aquí: la 2ª bloquea hasta el commit de la 1ª y luego ve el número ya asignado.
                    //    Lock transaccional → se libera solo al commit/rollback (sin fugas).
                    await _context.Database.ExecuteSqlInterpolatedAsync(
                        $@"SELECT pg_advisory_xact_lock({InvoiceLockNamespace}, {hireId});", ct);

                    // 2) Releer el número YA persistido bajo el lock. Si existe (reintento tras
                    //    reinicio/redeploy, u otra réplica ganó) → reusar, sin QUEMAR correlativo.
                    var existing = await _context.SearchHires
                        .IgnoreQueryFilters()
                        .Where(sh => sh.Id == hireId)
                        .Select(sh => sh.InvoiceNumber)
                        .FirstOrDefaultAsync(ct);
                    if (!string.IsNullOrEmpty(existing))
                    {
                        invoiceNumber = existing!;
                        await tx.CommitAsync(ct);
                        return;
                    }

                    // 3) Sin número aún → asegurar la fila del contador (UPSERT idempotente).
                    await _context.Database.ExecuteSqlInterpolatedAsync($@"
                        INSERT INTO ""InvoiceCounters"" (""SeriesCode"", ""Year"", ""NextNumber"", ""CreatedAt"")
                        VALUES ({seriesCode}, {year}, 1, {DateTime.UtcNow})
                        ON CONFLICT (""SeriesCode"", ""Year"") DO NOTHING;
                    ", ct);

                    // 4) Lock del contador + leer NextNumber (mismo patrón que NextAsync).
                    var nextNumber = await _context.InvoiceCounters
                        .FromSqlInterpolated($@"
                            SELECT * FROM ""InvoiceCounters""
                            WHERE ""SeriesCode"" = {seriesCode} AND ""Year"" = {year}
                            FOR UPDATE
                        ")
                        .AsNoTracking()
                        .Select(c => c.NextNumber)
                        .FirstAsync(ct);

                    invoiceNumber = $"{prefixClean}-{year:D4}-{nextNumber:D6}";

                    // 5) Incrementar el contador Y asignar el número al hire EN LA MISMA TRANSACCIÓN.
                    //    El correlativo se quema (NextNumber+1) SOLO aquí, con uso garantizado porque el
                    //    mismo commit persiste SearchHires.InvoiceNumber. Elimina la ventana de hueco del
                    //    patrón antiguo (NextAsync commiteaba el incremento y el UPDATE condicional podía
                    //    descartar el número → correlativo quemado sin documento).
                    await _context.Database.ExecuteSqlInterpolatedAsync($@"
                        UPDATE ""InvoiceCounters""
                        SET ""NextNumber"" = ""NextNumber"" + 1, ""UpdatedAt"" = {DateTime.UtcNow}
                        WHERE ""SeriesCode"" = {seriesCode} AND ""Year"" = {year};
                    ", ct);

                    await _context.Database.ExecuteSqlInterpolatedAsync($@"
                        UPDATE ""SearchHires""
                        SET ""InvoiceNumber"" = {invoiceNumber}
                        WHERE ""Id"" = {hireId};
                    ", ct);

                    await tx.CommitAsync(ct);
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
