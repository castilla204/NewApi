using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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
        // 🛡️ Round 27 — R27-T27-1/R27-A9-1 FIX: cache la reserva del número correlativo por
        // hireId durante 6h para que Hangfire retry +60s/+300s/+600s reuse el mismo número.
        // Sin esto, cada retry consume un número correlativo nuevo → huecos en serie fiscal
        // (RD 1619/2012 art. 6.1.a) cuando IsVatRegistered=true se active.
        // ✅ FIX AUDITORÍA [M6] (2026-06-15): RESUELTA la limitación residual. La fuente de verdad
        // de la idempotencia es ahora la columna DURABLE SearchHire.InvoiceNumber (persistida con
        // UPDATE ... WHERE InvoiceNumber IS NULL antes de generar/enviar). Esto cubre el caso
        // cross-replica y el reinicio/redeploy entre reintentos. El IMemoryCache permanece SOLO
        // como optimización opcional (evita un SELECT extra), NO como fuente de verdad.
        private readonly IMemoryCache _memoryCache;
        private static readonly TimeSpan InvoiceNumberCacheTtl = TimeSpan.FromHours(6);

        public InvoiceService(
            AppDbContext context,
            IEmailService emailService,
            IInvoiceNumberService invoiceNumberService,
            Microsoft.Extensions.Options.IOptions<newApi.Configuration.PlatformFiscalProfile> fiscal,
            IMemoryCache memoryCache)
        {
            _context = context;
            _emailService = emailService;
            _invoiceNumberService = invoiceNumberService;
            _fiscal = fiscal?.Value ?? new newApi.Configuration.PlatformFiscalProfile();
            _memoryCache = memoryCache;

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
            //
            // 🛡️ Round 27 — R27-T27-1/R27-A9-1 FIX: idempotencia mediante MemoryCache.
            // ANTES: cada invocación llamaba NextAsync → quemaba un correlativo. Cuando
            // SendInvoiceByEmailBackgroundJob fallaba (SMTP timeout 60s, p.ej. Hostinger puerto 465)
            // Hangfire reintentaba 3 veces. Cada retry consumía un nuevo número correlativo. Para
            // una sola factura con 3 retries: números 42, 43, 44 quemados, sólo 44 enviado → huecos
            // 42 y 43 sin emitir documento ni audit trail (violación RD 1619/2012 art. 6.1.a).
            // AHORA: cache la reserva por hireId durante 6h. Los retries dentro de esa ventana
            // reusan el mismo número. Documentado en el comentario del campo _memoryCache que esto
            // es una defensa parcial (cubre retry intra-proceso, no cross-replica/restart).
            string invoiceNumber;
            if (_fiscal.IsReadyForFlip())
            {
                // ✅ FIX AUDITORÍA [M6] (2026-06-15): la FUENTE DE VERDAD de la idempotencia es ahora
                // la columna DURABLE SearchHire.InvoiceNumber, no IMemoryCache. Antes la única
                // "memoria" de qué correlativo corresponde al hire era una clave de caché in-process
                // (Program.cs AddMemoryCache, sin Redis/IDistributedCache): un redeploy/reinicio entre
                // reintentos Hangfire (+60s/+300s/+600s) o un retry aterrizando en OTRA réplica
                // (WorkerCount>=2, cola compartida en Postgres) encontraba la caché vacía → reejecutaba
                // NextAsync y QUEMABA un nuevo correlativo → hueco en la serie (RD 1619/2012 art. 6.1.a).
                // AHORA: si el hire ya tiene número persistido se reusa; si no, se reserva con NextAsync
                // y se PERSISTE antes de generar/enviar, protegido para concurrencia (solo asigna si
                // sigue null). El IMemoryCache queda como optimización opcional, NO como fuente de verdad.
                var cacheKey = $"R27-InvoiceNumber-Hire-{searchHire.Id}";

                if (!string.IsNullOrEmpty(searchHire.InvoiceNumber))
                {
                    // Fuente de verdad durable: el número ya fue reservado y persistido para este hire.
                    invoiceNumber = searchHire.InvoiceNumber;
                    _memoryCache.Set(cacheKey, invoiceNumber, InvoiceNumberCacheTtl);
                }
                else
                {
                    // ✅ FIX (hueco de numeración): reservar el correlativo Y asignarlo a
                    // SearchHire.InvoiceNumber ATÓMICAMENTE (una sola transacción con advisory lock por
                    // hire). Antes se llamaba NextAsync (que COMMITEABA el incremento) y luego un UPDATE
                    // condicional; si la carrera se perdía, el número quemado se descartaba → hueco en la
                    // serie (RD 1619/2012 art. 6.1.a). Ahora el número solo se quema si se usa.
                    invoiceNumber = await _invoiceNumberService.ReserveForHireAsync(searchHire.Id, _fiscal.InvoiceSeriesPrefix);
                    // Reflejar en la entidad ya cargada (el UPDATE crudo no actualiza el tracker).
                    searchHire.InvoiceNumber = invoiceNumber;
                    _memoryCache.Set(cacheKey, invoiceNumber, InvoiceNumberCacheTtl);
                }
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

            // 🛡️ Round 28 MUD-AI: símbolo de moneda dinámico para que la factura PDF
            // muestre el valor real cobrado (£/$/CHF/etc.), no un € fijo. Antes una factura
            // de un cliente UK por £100 aparecía como "100,00 €" — incorrecto fiscalmente.
            var pdfCurrencyIso = string.IsNullOrEmpty(searchHire.Currency) ? "EUR" : searchHire.Currency.ToUpperInvariant();
            var pdfCurrencySymbol = pdfCurrencyIso switch
            {
                "EUR" => "€",
                "USD" => "$",
                "GBP" => "£",
                "CAD" => "CA$",
                "CHF" => "CHF",
                "SEK" => "kr",
                "DKK" => "kr",
                "NOK" => "kr",
                "PLN" => "zł",
                "HUF" => "Ft",
                "CZK" => "Kč",
                "BGN" => "лв",
                "RON" => "lei",
                _ => pdfCurrencyIso // fallback: ISO code visible
            };

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
                                    table.Cell().Element(CellStyle).AlignRight().Text($"{baseSinIva:N2} {pdfCurrencySymbol}");

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
                                    table.Cell().Element(CellStyle).AlignRight().Text($"{baseSinIva:N2} {pdfCurrencySymbol}").FontSize(11);

                                    table.Cell().Element(CellStyle).Text($"IVA ({ivaPct}%):").FontSize(11);
                                    table.Cell().Element(CellStyle).AlignRight().Text($"{iva:N2} {pdfCurrencySymbol}").FontSize(11);

                                    table.Cell().Element(CellStyle).Text("TOTAL:").Bold().FontSize(12);
                                    table.Cell().Element(CellStyle).AlignRight().Text($"{total:N2} {pdfCurrencySymbol}").Bold().FontSize(12);

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

                var (subject, emailBody) = RenderInvoiceEmail(
                    searchHire.Client.Name,
                    searchHire.SearchService.ServiceType.Name,
                    searchHire.Expert?.Name ?? "Experto eliminado",
                    searchHire.Amount,
                    searchHire.Currency ?? "EUR",
                    searchHire.Client?.PreferredCurrency,
                    searchHireId);

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
        /// Render puro del email de factura (sin DB ni PDF) — usado por el envío real y por el preview admin.
        /// El cuerpo es idéntico al anterior; el botón "Ver detalles" adopta el estilo canónico del renderer compartido.
        /// </summary>
        public static (string subject, string html) RenderInvoiceEmail(
            string clientName, string serviceName, string expertName,
            decimal amount, string currency, string? preferredCurrency, int searchHireId)
        {
            var subject = "Factura y confirmación de contratación";
            var title = "¡Contratación completada!";
            // 🛡️ FIX [W36-INVOICE-HTML-ESCAPE] (auditoría 2026-07-13): escapar los nombres antes de
            // interpolarlos en el HTML del email. `expertName` (= User.Name del experto) es texto libre
            // editable con solo Trim+≤100 (sin saneo HTML) y este email va al CLIENTE → inyección HTML
            // cross-actor (enlaces de phishing / pixel de rastreo / spoofing) en un correo transaccional
            // de factura de máxima confianza. Misma clase que endureció W24, que sin embargo se saltó
            // este método (VIVO, a diferencia de los Render* muertos que sí tocó). serviceName/clientName
            // se escapan por paridad.
            var safeClientName = System.Net.WebUtility.HtmlEncode(clientName ?? string.Empty);
            var safeServiceName = System.Net.WebUtility.HtmlEncode(serviceName ?? string.Empty);
            var safeExpertName = System.Net.WebUtility.HtmlEncode(expertName ?? string.Empty);
            var chargeCurrency = string.IsNullOrEmpty(currency) ? "EUR" : currency.ToUpperInvariant();
            var amountFormatted = amount.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("es-ES"));
            var convertedLine = string.Empty;
            if (!string.IsNullOrEmpty(preferredCurrency)
                && !preferredCurrency.Equals(chargeCurrency, StringComparison.OrdinalIgnoreCase))
            {
                convertedLine = $@"<p style='margin:0 0 16px 0;color:#6B7280;font-size:13px;'>(Tu moneda preferida es {preferredCurrency.ToUpperInvariant()} — el cargo real es en {chargeCurrency}.)</p>";
            }
            var content = $@"
                <p style='margin:0 0 16px 0;'>Hola {safeClientName},</p>
                <p style='margin:0 0 16px 0;'>¡Gracias por confiar en Inspecciono! La contratación del servicio <strong>{safeServiceName}</strong> con el experto <strong>{safeExpertName}</strong> se ha procesado correctamente.</p>
                <table role='presentation' cellpadding='0' cellspacing='0' border='0' style='margin:0 0 16px 0;width:100%;'>
                    <tr>
                        <td class='panel' style='background-color:#F8FAFC;border-radius:8px;padding:14px 18px;'>
                            <span class='panel-label' style='font-size:13px;color:#6B7280;'>Monto cobrado</span><br>
                            <span class='amount-hero' style='font-size:22px;font-weight:700;color:#0F172A;letter-spacing:-0.2px;'>{amountFormatted} {chargeCurrency}</span>
                        </td>
                    </tr>
                </table>
                {convertedLine}
                <p style='margin:0 0 16px 0;'>Adjunto a este correo encontrarás la factura en formato PDF con el desglose de impuestos correspondiente.</p>
                <p style='margin:0 0 16px 0;'>El experto se pondrá en contacto contigo pronto para coordinar los detalles.</p>
                <p style='margin:0;font-size:13px;color:#6B7280;'>Si tienes alguna pregunta, no dudes en contactarnos.</p>";
            var html = EmailTemplateRenderer.GenerateEmailTemplate(title, content, "Ver detalles", $"https://inspecciono.com/hires/{searchHireId}");
            return (subject, html);
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

