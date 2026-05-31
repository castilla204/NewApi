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
        private readonly IInvoiceNumberService _invoiceNumberService;
        private readonly newApi.Configuration.PlatformFiscalProfile _fiscal;

        public InvoiceService(
            AppDbContext context,
            IEmailService emailService,
            IInvoiceNumberService invoiceNumberService,
            Microsoft.Extensions.Options.IOptions<newApi.Configuration.PlatformFiscalProfile> fiscal)
        {
            _context = context;
            _emailService = emailService;
            _invoiceNumberService = invoiceNumberService;
            _fiscal = fiscal?.Value ?? new newApi.Configuration.PlatformFiscalProfile();

            // Configurar QuestPDF
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public async Task<byte[]> GenerateInvoicePdfAsync(int searchHireId)
        {
            // Cargar datos de la contratación con todas las relaciones necesarias
            // ✅ FIX: Usar IgnoreQueryFilters para cargar incluso si el usuario fue eliminado
            var searchHire = await _context.SearchHires
                .IgnoreQueryFilters()
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

            // ✅ FIX: Validar que los datos necesarios no sean null (pueden estar anonimizados)
            if (searchHire.Client == null)
            {
                throw new InvalidOperationException($"No se puede generar factura para SearchHire {searchHireId}: el cliente fue eliminado o anonimizado. ClientId: {searchHire.ClientId}");
            }

            if (searchHire.SearchService == null)
            {
                throw new InvalidOperationException($"No se puede generar factura para SearchHire {searchHireId}: el servicio fue eliminado. SearchServiceId: {searchHire.SearchServiceId}");
            }

            if (searchHire.SearchService.ServiceType == null)
            {
                throw new InvalidOperationException($"No se puede generar factura para SearchHire {searchHireId}: el tipo de servicio fue eliminado. ServiceTypeId: {searchHire.SearchService.ServiceTypeId}");
            }

            if (searchHire.SearchService.Category == null)
            {
                throw new InvalidOperationException($"No se puede generar factura para SearchHire {searchHireId}: la categoría fue eliminada. CategoryId: {searchHire.SearchService.CategoryId}");
            }

            // Datos para la factura
            // 🔧 FISCAL FLIP: numeración condicional.
            //   IsReadyForFlip()=false → pseudo-número estable basado en hire ID (NO correlativo, NO fiscal).
            //   IsReadyForFlip()=true  → reserva número correlativo persistente desde InvoiceCounters (sin huecos).
            // NOTA: si flip activo, llamar a GenerateInvoicePdfAsync MÁS DE UNA VEZ por hire consume números
            // correlativos extra (TODO: persistir InvoiceNumber en SearchHire la 1ª vez para reutilizarlo).
            string invoiceNumber;
            if (_fiscal.IsReadyForFlip())
            {
                invoiceNumber = await _invoiceNumberService.NextAsync(_fiscal.InvoiceSeriesPrefix);
            }
            else
            {
                invoiceNumber = $"FAC-{searchHire.Id:D6}"; // recibo simple, NO es factura formal
            }
            var invoiceDate = searchHire.CreatedAt;
            var clientName = searchHire.Client.Name ?? "Cliente eliminado";
            var clientEmail = searchHire.Client.Email ?? "email@eliminado.com";
            var serviceName = searchHire.SearchService.ServiceType.Name ?? "Servicio eliminado";
            var serviceCategory = searchHire.SearchService.Category.Name ?? "Categoría eliminada";
            var total = searchHire.Amount; // bruto con IVA incluido (si lo hay)
            // 🔧 FIX internacional: NO inventar 21% de IVA. Derivar del impuesto REAL (Stripe Tax).
            // 🛡️ N21 FIX: si la plataforma YA está registrada fiscalmente (flip H aplicado) pero el
            // SearchHire es LEGACY (sin TaxAmount/BaseAmount poblados antes de Stripe Tax), la factura
            // sale con IVA=0% — incumplimiento fiscal silencioso. Loguear critical para alerta al admin
            // (debe regenerar la factura manualmente con los datos correctos o bloquear la emisión).
            // Pre-flip (IsVatRegistered=false): recibo simple sin IVA es correcto → log info silencioso.
            if (searchHire.TaxAmount == null && _fiscal.IsReadyForFlip())
            {
                Console.WriteLine($"[INVOICE SERVICE] CRITICAL N21: SearchHire {searchHire.Id} sin TaxAmount/BaseAmount " +
                    $"pero plataforma está registrada (IsVatRegistered=true). Factura emitida con IVA=0% como fallback. " +
                    $"ACCIÓN ADMIN: regenerar manualmente con datos fiscales correctos o bloquear emisión.");
            }
            var iva = searchHire.TaxAmount ?? 0m;
            var baseSinIva = searchHire.BaseAmount ?? Math.Round(total - iva, 2);
            var ivaPct = (baseSinIva > 0m && iva > 0m)
                ? (int)Math.Round(iva / baseSinIva * 100m, MidpointRounding.AwayFromZero)
                : 0;

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
                                    table.Cell().Element(CellStyle).AlignRight().Text($"{baseSinIva:N2} €");

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

                                    table.Cell().Element(CellStyle).Text("Base imponible:").FontSize(11);
                                    table.Cell().Element(CellStyle).AlignRight().Text($"{baseSinIva:N2} €").FontSize(11);

                                    table.Cell().Element(CellStyle).Text($"IVA ({ivaPct}%):").FontSize(11);
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

                                // 🔧 FISCAL FLIP: si la plataforma está dada de alta y rellena el perfil,
                                // pintar datos fiscales del emisor + NIF cliente (si lo tenemos) + mención
                                // reverse-charge cuando IVA=0 y cliente tiene NIF intracomunitario no-ES.
                                // Pre-flip: leyenda "documento informativo" (no es factura fiscal).
                                if (_fiscal.IsReadyForFlip())
                                {
                                    totalesColumn.Item().PaddingTop(15).Text($"Emisor: {_fiscal.LegalName}").FontSize(9);
                                    totalesColumn.Item().Text($"NIF: {_fiscal.Nif}").FontSize(9);
                                    totalesColumn.Item().Text($"{_fiscal.FiscalAddress.Street}, {_fiscal.FiscalAddress.PostalCode} {_fiscal.FiscalAddress.City} ({_fiscal.FiscalAddress.Province}) — {_fiscal.FiscalAddress.Country}").FontSize(9);
                                    if (!string.IsNullOrWhiteSpace(_fiscal.IaeCode))
                                        totalesColumn.Item().Text($"IAE: {_fiscal.IaeCode}").FontSize(9);
                                    if (!string.IsNullOrWhiteSpace(searchHire.ClientVatNumber))
                                        totalesColumn.Item().Text($"NIF cliente: {searchHire.ClientVatCountryCode}{searchHire.ClientVatNumber}").FontSize(9);
                                    // 🛡️ Round 13 — N9 FIX: solo imprimir el texto de "operación intracomunitaria"
                                    // si el país del VAT del cliente es EFECTIVAMENTE de la UE. Antes bastaba
                                    // con country != ES → habría disparado erróneamente para clientes con
                                    // VAT GB (post-Brexit), CH, NO, LI, US, CA, etc., que NO son operaciones
                                    // intracomunitarias y necesitan distinto texto/referencia legal.
                                    var clientVatCountry = searchHire.ClientVatCountryCode;
                                    var clientIsEuButNotEs = newApi.Common.EuVatCountries.IsEu(clientVatCountry)
                                        && !string.Equals(clientVatCountry, "ES", StringComparison.OrdinalIgnoreCase);
                                    if (iva == 0m && !string.IsNullOrWhiteSpace(searchHire.ClientVatNumber) && clientIsEuButNotEs)
                                    {
                                        totalesColumn.Item().PaddingTop(5)
                                            .Text("Operación intracomunitaria — Inversión del sujeto pasivo (art. 84.Uno.2º Ley 37/1992 / art. 196 Dir. 2006/112/CE). IVA NO repercutido.")
                                            .FontSize(8).Italic();
                                    }
                                }
                                else
                                {
                                    // Pre-flip: leyenda explícita de que no es factura fiscal (la plataforma aún no es empresa).
                                    totalesColumn.Item().PaddingTop(10)
                                        .Text("Documento informativo de pago. No constituye factura a efectos fiscales.")
                                        .FontSize(8).Italic();
                                }
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
                // ✅ FIX: Validar primero si el SearchHire existe y tiene datos válidos
                var searchHire = await _context.SearchHires
                    .IgnoreQueryFilters()
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId);

                if (searchHire == null)
                {
                    throw new ArgumentException($"SearchHire con ID {searchHireId} no encontrado");
                }

                // ✅ FIX: Validar que el cliente no sea null (puede estar anonimizado)
                if (searchHire.Client == null)
                {
                    throw new InvalidOperationException($"No se puede enviar factura para SearchHire {searchHireId}: el cliente fue eliminado o anonimizado. ClientId: {searchHire.ClientId}");
                }

                // ✅ FIX: Validar que SearchService y ServiceType no sean null
                if (searchHire.SearchService == null || searchHire.SearchService.ServiceType == null)
                {
                    throw new InvalidOperationException($"No se puede enviar factura para SearchHire {searchHireId}: el servicio fue eliminado. SearchServiceId: {searchHire.SearchServiceId}");
                }

                // Generar PDF
                // 🔧 FISCAL FLIP: la numeración correlativa (si flip activo) se reserva DENTRO de
                // GenerateInvoicePdfAsync — no la duplicamos aquí. El nombre del fichero adjunto usa una
                // etiqueta estable por hire ID (no es el número fiscal): evita consumir un 2º número en
                // reenvíos del email.
                var pdfBytes = await GenerateInvoicePdfAsync(searchHireId);

                var invoiceNumber = $"FAC-{searchHire.Id:D6}"; // label estable para el nombre del adjunto, NO número fiscal
                var subject = "Factura y confirmación de contratación";
                var title = "¡Contratación completada!";
                var serviceName = searchHire.SearchService.ServiceType.Name;
                var expertName = searchHire.Expert?.Name ?? "Experto eliminado";

                var content = $@"
                    <p style='margin:0 0 12px 0;'>Hola {searchHire.Client.Name},</p>
                    <p style='margin:0 0 12px 0;'>¡Gracias por confiar en Inspecciono! La contratación del servicio <strong>{serviceName}</strong> con el experto <strong>{expertName}</strong> se ha procesado correctamente.</p>
                    <p style='margin:0 0 12px 0;'>Adjunto a este correo encontrarás la factura en formato PDF con el desglose de impuestos correspondiente.</p>
                    <p style='margin:0 0 16px 0;'>El experto se pondrá en contacto contigo pronto para coordinar los detalles.</p>
                    <p style='margin:0;font-size:13px;color:#6B7280;'>Si tienes alguna pregunta, no dudes en contactarnos.</p>";

                var emailBody = GenerateEmailTemplate(title, content, "Ver detalles", $"https://inspecciono.com/hires/{searchHireId}", "📄");

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

        private string GenerateEmailTemplate(string title, string content, string? actionText = null, string? actionUrl = null, string headerIcon = "📢")
        {
            var year = DateTime.UtcNow.Year.ToString();
            string templateHtml;
            try 
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Resources", "EmailTemplate.html");
                if (File.Exists(path))
                    templateHtml = File.ReadAllText(path);
                else
                    templateHtml = "<html><body><h1>{{TITLE}}</h1><div>{{CONTENT}}</div>{{ACTION_BUTTON}}</body></html>";
            }
            catch
            {
                templateHtml = "<html><body><h1>{{TITLE}}</h1><div>{{CONTENT}}</div>{{ACTION_BUTTON}}</body></html>";
            }

            var actionButtonHtml = "";
            if (!string.IsNullOrEmpty(actionText) && !string.IsNullOrEmpty(actionUrl))
            {
                actionButtonHtml = $@"
                    <table align='center' border='0' cellpadding='0' cellspacing='0' style='border-collapse:collapse;border-spacing:0;padding:16px 0 0 0;text-align:center;vertical-align:top;width:100%'>
                        <tbody>
                            <tr>
                                <td align='center' style='padding:0'>
                                    <!--[if mso]>
                                    <v:roundrect xmlns:v='urn:schemas-microsoft-com:vml' xmlns:w='urn:schemas-microsoft-com:office:word' href='{actionUrl}' style='height:36px;v-text-anchor:middle;width:180px;' arcsize='15%' strokecolor='#2563EB' fillcolor='#2563EB'>
                                    <w:anchorlock/>
                                    <center style='color:#ffffff;font-family:Helvetica,Arial,sans-serif;font-size:13px;font-weight:600;'>{actionText}</center>
                                    </v:roundrect>
                                    <![endif]-->
                                    <!--[if !mso]><!-->
                                    <a href='{actionUrl}' style='background-color:#2563EB;border-radius:6px;color:#ffffff;display:inline-block;font-family:Helvetica,Arial,sans-serif;font-size:13px;font-weight:600;line-height:36px;mso-hide:all;padding:0 24px;text-align:center;text-decoration:none;'>{actionText}</a>
                                    <!--<![endif]-->
                                </td>
                            </tr>
                        </tbody>
                    </table>";
            }

            return templateHtml
                .Replace("{{TITLE}}", title)
                .Replace("{{CONTENT}}", content)
                .Replace("{{ACTION_BUTTON}}", actionButtonHtml)
                .Replace("{{YEAR}}", year);
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
            catch (InvalidOperationException ex) when (ex.Message.Contains("eliminado") || ex.Message.Contains("anonimizado"))
            {
                // ✅ FIX: No reintentar si el cliente fue eliminado/anonimizado - es un error permanente
                Console.WriteLine($"[INVOICE SERVICE] [HANGFIRE ERROR] ERROR permanente al enviar factura. SearchHireId: {searchHireId}, Error: {ex.Message}. No se reintentará.");
                // No re-lanzar la excepción para que Hangfire no siga reintentando
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[INVOICE SERVICE] [HANGFIRE ERROR] ERROR al enviar factura. SearchHireId: {searchHireId}, Error: {ex.Message}");
                throw; // Re-lanzar para que Hangfire reintente solo errores temporales
            }
        }
    }
}

