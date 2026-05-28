namespace newApi.Services
{
    /// <summary>
    /// Numerador persistente, correlativo, SIN HUECOS. Para facturación fiscal española
    /// (art. 6.1.a RD 1619/2012, art. 14 sobre series). USO SÓLO con PlatformFiscal.IsVatRegistered=true.
    /// </summary>
    public interface IInvoiceNumberService
    {
        /// <summary>
        /// Reserva y devuelve el siguiente número de la serie indicada para el año UTC actual,
        /// con formato "{seriesPrefix}{YYYY}-{NNNNNN}" (6 dígitos zero-padded).
        ///
        /// Garantías:
        ///   • Correlativo sin huecos (SELECT ... FOR UPDATE por fila + UPSERT idempotente).
        ///   • Si NO hay fila (SeriesCode, Year), la crea con NextNumber=1.
        ///   • Lanza InvalidOperationException si IsVatRegistered=false (fail-fast).
        ///
        /// IMPORTANTE: el commit del incremento ocurre DENTRO de este método. Si tras llamarlo
        /// el caller hace rollback, el número queda "perdido" (provoca hueco). Por eso debería
        /// llamarse como ÚLTIMO paso antes del envío del PDF; si el envío falla, registrar
        /// como "factura emitida-no-entregada" (reintento de envío, NUNCA de numeración).
        /// </summary>
        Task<string> NextAsync(string seriesPrefix, CancellationToken ct = default);
    }
}
