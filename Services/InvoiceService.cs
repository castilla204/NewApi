using Hangfire;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    public class InvoiceService : IInvoiceService
    {
        private readonly AppDbContext _context;
        private readonly IEmailService _emailService;

        public InvoiceService(AppDbContext context, IEmailService emailService)
        {
            _context = context;
            _emailService = emailService;
            
            // Configurar QuestPDF
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public async Task<byte[]> GenerateInvoicePdfAsync(int searchHireId)
        {
            // Cargar datos de la contratación con todas las relaciones necesarias
            var searchHire = await _context.SearchHires
                .Include(sh => sh.Client)
                .Include(sh => sh.Expert)
                .Include(sh => sh.SearchService)
                    .ThenInclude(ss => ss.ServiceType)
                .Include(sh => sh.SearchService)
                    .ThenInclude(ss => ss.Category)
                .Include(sh => sh.Search)
                .FirstOrDefaultAsync(sh => sh.Id == searchHireId);

            if (searchHire == null)
            {
                throw new ArgumentException($"SearchHire con ID {searchHireId} no encontrado");
            }

            // Datos para la factura
            var invoiceNumber = $"FAC-{searchHire.Id:D6}";
            var invoiceDate = searchHire.CreatedAt;
            var clientName = searchHire.Client.Name;
            var clientEmail = searchHire.Client.Email;
            var serviceName = searchHire.SearchService.ServiceType.Name;
            var serviceCategory = searchHire.SearchService.Category.Name;
            var amount = searchHire.Amount;
            var iva = amount * 0.21m; // IVA 21%
            var total = amount + iva;

            // Generar PDF con QuestPDF
            var pdfBytes = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);

                    page.Header().Element(ComposeHeader);
                    page.Content().Element(ComposeContent);
                    page.Footer().Element(ComposeFooter);

                    void ComposeHeader(IContainer container)
                    {
                        container.Row(row =>
                        {
                            row.RelativeColumn().Column(column =>
                            {
                                column.Item().Text("FACTURA").FontSize(24).Bold().FontColor(Colors.Blue.Darken3);
                                column.Item().Text($"Número: {invoiceNumber}").FontSize(12);
                                column.Item().Text($"Fecha: {invoiceDate:dd/MM/yyyy}").FontSize(12);
                            });

                            row.ConstantColumn(100).Column(column =>
                            {
                                column.Item().AlignRight().Text("Inspecciono").FontSize(16).Bold();
                                column.Item().AlignRight().Text("info@inspecciono.com").FontSize(10);
                                column.Item().AlignRight().Text("www.inspecciono.com").FontSize(10);
                            });
                        });
                    }

                    void ComposeContent(IContainer container)
                    {
                        container.Column(column =>
                        {
                            column.Spacing(20);

                            // Datos del cliente
                            column.Item().PaddingBottom(10).BorderBottom(1).BorderColor(Colors.Grey.Lighten1).Column(clienteColumn =>
                            {
                                clienteColumn.Item().Text("DATOS DEL CLIENTE").FontSize(14).Bold();
                                clienteColumn.Item().Text($"Nombre: {clientName}").FontSize(11);
                                clienteColumn.Item().Text($"Email: {clientEmail}").FontSize(11);
                            });

                            // Datos del servicio
                            column.Item().Column(servicioColumn =>
                            {
                                servicioColumn.Item().PaddingBottom(5).Text("DETALLE DEL SERVICIO").FontSize(14).Bold();
                                
                                servicioColumn.Item().Table(table =>
                                {
                                    table.ColumnsDefinition(columns =>
                                    {
                                        columns.RelativeColumn();
                                        columns.RelativeColumn();
                                    });

                                    table.Header(header =>
                                    {
                                        header.Cell().Element(CellStyle).Text("Concepto").Bold();
                                        header.Cell().Element(CellStyle).AlignRight().Text("Importe").Bold();
                                    });

                                    table.Cell().Element(CellStyle).Text($"Servicio: {serviceName}");
                                    table.Cell().Element(CellStyle).AlignRight().Text($"{amount:N2} €");

                                    table.Cell().Element(CellStyle).Text($"Categoría: {serviceCategory}");
                                    table.Cell().Element(CellStyle).AlignRight().Text("");

                                    table.Cell().Element(CellStyle).Text($"Contratación ID: #{searchHire.Id}");
                                    table.Cell().Element(CellStyle).AlignRight().Text("");

                                    static IContainer CellStyle(IContainer container)
                                    {
                                        return container
                                            .BorderBottom(1)
                                            .BorderColor(Colors.Grey.Lighten2)
                                            .PaddingVertical(5)
                                            .PaddingHorizontal(2);
                                    }
                                });
                            });

                            // Totales
                            column.Item().AlignRight().Column(totalesColumn =>
                            {
                                totalesColumn.Item().Width(200).Table(table =>
                                {
                                    table.ColumnsDefinition(columns =>
                                    {
                                        columns.RelativeColumn();
                                        columns.RelativeColumn();
                                    });

                                    table.Cell().Element(CellStyle).Text("Subtotal:").FontSize(11);
                                    table.Cell().Element(CellStyle).AlignRight().Text($"{amount:N2} €").FontSize(11);

                                    table.Cell().Element(CellStyle).Text("IVA (21%):").FontSize(11);
                                    table.Cell().Element(CellStyle).AlignRight().Text($"{iva:N2} €").FontSize(11);

                                    table.Cell().Element(CellStyle).Text("TOTAL:").Bold().FontSize(12);
                                    table.Cell().Element(CellStyle).AlignRight().Text($"{total:N2} €").Bold().FontSize(12);

                                    static IContainer CellStyle(IContainer container)
                                    {
                                        return container
                                            .BorderTop(1)
                                            .BorderColor(Colors.Grey.Lighten2)
                                            .PaddingVertical(3)
                                            .PaddingHorizontal(2);
                                    }
                                });
                            });
                        });
                    }

                    void ComposeFooter(IContainer container)
                    {
                        container.AlignCenter().Column(column =>
                        {
                            column.Item().Text("Gracias por confiar en Inspecciono").FontSize(10).Italic();
                            column.Item().Text($"Factura generada el {DateTime.UtcNow:dd/MM/yyyy HH:mm} UTC").FontSize(8).FontColor(Colors.Grey.Medium);
                        });
                    }
                });
            })
            .GeneratePdf();

            return pdfBytes;
        }

        public async Task SendInvoiceByEmailAsync(int searchHireId, string toEmail)
        {
            try
            {
                // Generar PDF
                var pdfBytes = await GenerateInvoicePdfAsync(searchHireId);

                // Convertir a base64 para adjuntar en email
                var pdfBase64 = Convert.ToBase64String(pdfBytes);

                // Obtener datos para el email
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Client)
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.Category)
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId);

                if (searchHire == null)
                {
                    throw new ArgumentException($"SearchHire con ID {searchHireId} no encontrado");
                }

                var invoiceNumber = $"FAC-{searchHire.Id:D6}";
                var subject = $"Factura {invoiceNumber} - Inspecciono";
                
                var iva = searchHire.Amount * 0.21m;
                var total = searchHire.Amount + iva;
                
                var emailBody = $@"
<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <meta http-equiv='X-UA-Compatible' content='IE=edge'>
    <!--[if mso]>
    <style type='text/css'>
        body, table, td {{font-family: Arial, Helvetica, sans-serif !important;}}
    </style>
    <![endif]-->
</head>
<body style='margin: 0; padding: 0; background-color: #f5f7fa; font-family: -apple-system, BlinkMacSystemFont, ''Segoe UI'', Roboto, ''Helvetica Neue'', Arial, sans-serif;'>
    <table role='presentation' cellspacing='0' cellpadding='0' border='0' width='100%' style='background-color: #f5f7fa;'>
        <tr>
            <td align='center' style='padding: 40px 20px;'>
                <table role='presentation' cellspacing='0' cellpadding='0' border='0' width='100%' style='max-width: 600px; background-color: #ffffff; border: 1px solid #e1e8ed;'>
                    
                    <!-- Company Header -->
                    <tr>
                        <td style='background-color: #1e3a5f; padding: 30px 40px;'>
                            <table role='presentation' cellspacing='0' cellpadding='0' border='0' width='100%'>
                                <tr>
                                    <td>
                                        <h1 style='margin: 0; font-size: 28px; font-weight: 700; color: #ffffff; letter-spacing: 0.5px;'>INSPECCIONO</h1>
                                        <p style='margin: 8px 0 0 0; font-size: 13px; color: #a0b8c8; font-weight: 400; text-transform: uppercase; letter-spacing: 1px;'>FACTURA</p>
                                    </td>
                                    <td align='right' style='vertical-align: top;'>
                                        <p style='margin: 0; font-size: 24px; font-weight: 700; color: #ffffff;'>{invoiceNumber}</p>
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                    
                    <!-- Invoice Info Section -->
                    <tr>
                        <td style='padding: 35px 40px; background-color: #ffffff;'>
                            <table role='presentation' cellspacing='0' cellpadding='0' border='0' width='100%'>
                                <tr>
                                    <td width='50%' style='vertical-align: top; padding-right: 20px;'>
                                        <p style='margin: 0 0 8px 0; font-size: 11px; color: #6b7280; text-transform: uppercase; letter-spacing: 0.5px; font-weight: 600;'>Facturado a</p>
                                        <p style='margin: 0 0 20px 0; font-size: 15px; color: #111827; font-weight: 600; line-height: 1.5;'>{searchHire.Client.Name}</p>
                                        <p style='margin: 0; font-size: 13px; color: #6b7280; line-height: 1.6;'>{searchHire.Client.Email}</p>
                                    </td>
                                    <td width='50%' style='vertical-align: top; padding-left: 20px; text-align: right;'>
                                        <p style='margin: 0 0 8px 0; font-size: 11px; color: #6b7280; text-transform: uppercase; letter-spacing: 0.5px; font-weight: 600;'>Fecha de emisión</p>
                                        <p style='margin: 0 0 20px 0; font-size: 15px; color: #111827; font-weight: 600;'>{searchHire.CreatedAt:dd/MM/yyyy}</p>
                                        <p style='margin: 0; font-size: 11px; color: #6b7280; text-transform: uppercase; letter-spacing: 0.5px; font-weight: 600;'>Número de factura</p>
                                        <p style='margin: 4px 0 0 0; font-size: 15px; color: #111827; font-weight: 600;'>{invoiceNumber}</p>
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                    
                    <!-- Service Details -->
                    <tr>
                        <td style='padding: 0 40px 30px 40px; background-color: #ffffff;'>
                            <table role='presentation' cellspacing='0' cellpadding='0' border='0' width='100%' style='border: 1px solid #e5e7eb;'>
                                <tr>
                                    <td style='background-color: #f9fafb; padding: 12px 20px; border-bottom: 1px solid #e5e7eb;'>
                                        <p style='margin: 0; font-size: 11px; color: #6b7280; text-transform: uppercase; letter-spacing: 0.5px; font-weight: 600;'>Descripción del servicio</p>
                                    </td>
                                </tr>
                                <tr>
                                    <td style='padding: 20px;'>
                                        <table role='presentation' cellspacing='0' cellpadding='0' border='0' width='100%'>
                                            <tr>
                                                <td style='padding-bottom: 12px; border-bottom: 1px solid #f3f4f6;'>
                                                    <p style='margin: 0 0 6px 0; font-size: 15px; color: #111827; font-weight: 600;'>{searchHire.SearchService.ServiceType.Name}</p>
                                                    <p style='margin: 0; font-size: 13px; color: #6b7280;'>Categoría: {searchHire.SearchService.Category?.Name ?? "N/A"}</p>
                                                </td>
                                            </tr>
                                        </table>
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                    
                    <!-- Amount Summary -->
                    <tr>
                        <td style='padding: 0 40px 35px 40px; background-color: #ffffff;'>
                            <table role='presentation' cellspacing='0' cellpadding='0' border='0' width='100%'>
                                <tr>
                                    <td align='right' style='padding-bottom: 15px;'>
                                        <table role='presentation' cellspacing='0' cellpadding='0' border='0' style='width: 250px;'>
                                            <tr>
                                                <td style='padding: 8px 0;'>
                                                    <p style='margin: 0; font-size: 14px; color: #6b7280; text-align: right;'>Subtotal</p>
                                                </td>
                                                <td style='padding: 8px 0; width: 100px; text-align: right;'>
                                                    <p style='margin: 0; font-size: 14px; color: #111827; font-weight: 500;'>{searchHire.Amount:N2} €</p>
                                                </td>
                                            </tr>
                                            <tr>
                                                <td style='padding: 8px 0;'>
                                                    <p style='margin: 0; font-size: 14px; color: #6b7280; text-align: right;'>IVA (21%)</p>
                                                </td>
                                                <td style='padding: 8px 0; text-align: right;'>
                                                    <p style='margin: 0; font-size: 14px; color: #111827; font-weight: 500;'>{iva:N2} €</p>
                                                </td>
                                            </tr>
                                            <tr>
                                                <td style='padding: 20px 0 8px 0; border-top: 2px solid #1e3a5f;'>
                                                    <p style='margin: 0; font-size: 16px; color: #111827; font-weight: 700; text-align: right;'>TOTAL</p>
                                                </td>
                                                <td style='padding: 20px 0 8px 0; border-top: 2px solid #1e3a5f; text-align: right;'>
                                                    <p style='margin: 0; font-size: 20px; color: #1e3a5f; font-weight: 700;'>{total:N2} €</p>
                                                </td>
                                            </tr>
                                        </table>
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                    
                    <!-- Attachment Notice -->
                    <tr>
                        <td style='padding: 0 40px 35px 40px; background-color: #ffffff;'>
                            <table role='presentation' cellspacing='0' cellpadding='0' border='0' width='100%' style='background-color: #f0f9ff; border-left: 4px solid #1e3a5f;'>
                                <tr>
                                    <td style='padding: 18px 20px;'>
                                        <p style='margin: 0; font-size: 13px; color: #1e3a5f; line-height: 1.6;'>
                                            <strong>Archivo adjunto:</strong> La factura en formato PDF está adjunta a este correo electrónico. Puede descargarla y guardarla para sus registros contables.
                                        </p>
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                    
                    <!-- Footer -->
                    <tr>
                        <td style='background-color: #f9fafb; padding: 30px 40px; border-top: 1px solid #e5e7eb;'>
                            <table role='presentation' cellspacing='0' cellpadding='0' border='0' width='100%'>
                                <tr>
                                    <td style='padding-bottom: 15px;'>
                                        <p style='margin: 0; font-size: 16px; font-weight: 700; color: #111827;'>Inspecciono</p>
                                    </td>
                                </tr>
                                <tr>
                                    <td style='padding-bottom: 8px;'>
                                        <p style='margin: 0; font-size: 13px; color: #6b7280;'>info@inspecciono.com</p>
                                    </td>
                                </tr>
                                <tr>
                                    <td>
                                        <p style='margin: 15px 0 0 0; font-size: 11px; color: #9ca3af; line-height: 1.6;'>
                                            Este es un correo electrónico automático generado por nuestro sistema. Por favor, no responda a este mensaje.<br>
                                            Factura generada el {DateTime.UtcNow:dd/MM/yyyy HH:mm} UTC
                                        </p>
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                    
                </table>
            </td>
        </tr>
    </table>
</body>
</html>";

                // Enviar email con PDF adjunto
                var fileName = $"Factura_{invoiceNumber}.pdf";
                await _emailService.SendEmailWithAttachmentAsync(
                    toEmail, 
                    subject, 
                    emailBody, 
                    pdfBytes, 
                    fileName, 
                    "application/pdf", 
                    isHtml: true
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[INVOICE SERVICE] ERROR al enviar factura: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Método para Hangfire: Envía la factura por email en segundo plano
        /// Este método es invocado por Hangfire y no bloquea la API
        /// </summary>
        [Hangfire.AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 600 })]
        public async Task SendInvoiceByEmailBackgroundJob(int searchHireId, string toEmail)
        {
            try
            {
                Console.WriteLine($"[INVOICE SERVICE] [HANGFIRE] Iniciando envio de factura en segundo plano. SearchHireId: {searchHireId}, To: {toEmail}");
                await SendInvoiceByEmailAsync(searchHireId, toEmail);
                Console.WriteLine($"[INVOICE SERVICE] [HANGFIRE SUCCESS] Factura enviada exitosamente. SearchHireId: {searchHireId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[INVOICE SERVICE] [HANGFIRE ERROR] ERROR al enviar factura. SearchHireId: {searchHireId}, Error: {ex.Message}");
                throw; // Re-lanzar para que Hangfire reintente
            }
        }
    }
}

