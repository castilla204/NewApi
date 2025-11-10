namespace newApi.Services
{
    public interface IInvoiceService
    {
        /// <summary>
        /// Genera un PDF de factura para una contratación
        /// </summary>
        Task<byte[]> GenerateInvoicePdfAsync(int searchHireId);
        
        /// <summary>
        /// Envía la factura por email al cliente
        /// </summary>
        Task SendInvoiceByEmailAsync(int searchHireId, string toEmail);
        
        /// <summary>
        /// Método para Hangfire: Envía la factura por email en segundo plano
        /// </summary>
        Task SendInvoiceByEmailBackgroundJob(int searchHireId, string toEmail);
    }
}

