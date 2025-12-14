using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Controllers
{
    /// <summary>
    /// Controlador para documentos legales dinámicos (Términos, Privacidad, Aviso Legal)
    /// Cumple con: LSSI-CE, RGPD, LOPD-GDD
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class LegalController : ControllerBase
    {
        private readonly AppDbContext _context;
        
        // Datos de la empresa (pueden moverse a configuración)
        private const string COMPANY_NAME = "Inspecciono";
        private const string COMPANY_WEBSITE = "inspecciono.com";
        private const string COMPANY_EMAIL = "info@inspecciono.com";
        private const string COMPANY_SUPPORT_EMAIL = "soporte@inspecciono.com";

        public LegalController(AppDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Obtiene los Términos y Condiciones actualizados dinámicamente con las tarifas vigentes.
        /// Cumple con LSSI-CE (Ley 34/2002)
        /// </summary>
        [HttpGet("terms")]
        public async Task<IActionResult> GetTermsAndConditions()
        {
            try
            {
                // 1. OBTENER TODAS LAS CONFIGURACIONES ACTIVAS AUTOMÁTICAMENTE
                // Traemos todas las configuraciones globales (sin categoría específica) que estén activas
                var allConfigs = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Where(sc => sc.IsActive && sc.CategoryId == null && sc.ServiceTypeCategoryId == null)
                    .ToListAsync();

                // 2. Definir descripciones legibles para los estados conocidos
                var statusDescriptions = new Dictionary<string, string>
                {
                    { "completed", "Servicio completado exitosamente" },
                    { "appointment_completed_without_client_approval", "Servicio completado (cierre automático)" },
                    
                    // Cancelaciones Cliente
                    { "appointment_cancelled_by_client_no_proposal", "Cancelación por Cliente: No propone fecha (Abandono)" },
                    { "appointment_cancelled_by_client_second", "Cancelación por Cliente: Tardía / Cita ya confirmada" },
                    { "cancelled_by_client_account_delete", "Cancelación por Cliente: Eliminación de cuenta" },
                    
                    // Cancelaciones Experto
                    { "appointment_cancelled_by_expert", "Cancelación por Experto: Genérica" },
                    { "appointment_cancelled_by_expert_no_response", "Cancelación por Experto: No responde a solicitud" },
                    { "appointment_cancelled_by_expert_rejection", "Cancelación por Experto: Rechazo de cita" },
                    { "appointment_cancelled_by_expert_second", "Cancelación por Experto: Cancelación tras confirmar" },
                    { "appointment_cancelled_by_no_report", "Cancelación por Experto: No entrega informe" },
                    { "cancelled_by_expert_account_delete", "Cancelación por Experto: Eliminación de cuenta" },

                    // Disputas
                    { "dispute_resolved_client", "Disputa resuelta a favor del Cliente" },
                    { "dispute_resolved_expert", "Disputa resuelta a favor del Experto" },
                    
                    // Otros
                    { "cancelled", "Cancelación Genérica" }
                };

                // 3. Generar filas de la tabla dinámicamente
                var tableRows = "";
                
                // Agrupar y ordenar para que salga bonito (primero completados, luego cancelaciones cliente, luego experto, luego disputas)
                var orderedConfigs = allConfigs
                    .OrderBy(c => 
                    {
                        var s = c.Status.StatusValue;
                        if (s.Contains("completed")) return 1;
                        if (s.Contains("client")) return 2;
                        if (s.Contains("expert")) return 3;
                        if (s.Contains("dispute")) return 4;
                        return 5;
                    })
                    .ToList();

                foreach (var config in orderedConfigs)
                {
                    var statusKey = config.Status.StatusValue;
                    
                    // Si tenemos descripción manual la usamos, si no, formateamos el técnico
                    string description;
                    if (statusDescriptions.TryGetValue(statusKey, out var desc))
                    {
                        description = desc;
                    }
                    else
                    {
                        // Fallback inteligente: "appointment_cancelled_foo" -> "Appointment Cancelled Foo"
                        description = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                            statusKey.Replace("_", " ").Replace("appointment", "").Trim()
                        );
                    }

                    tableRows += $@"
                        <tr>
                            <td>{description}</td>
                            <td>{config.ClientPercentage:0.##}%</td>
                            <td>{config.ExpertPercentage:0.##}%</td>
                            <td>{config.PlatformPercentage:0.##}%</td>
                        </tr>";
                }

                // Obtener fee base para el texto introductorio (usamos el de 'completed' o 5 por defecto)
                var successConfig = allConfigs.FirstOrDefault(c => c.Status.StatusValue == "completed");
                decimal platformFee = successConfig?.PlatformPercentage ?? 5;

                var year = DateTime.UtcNow.Year;

                var htmlContent = $@"
<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='UTF-8'>
    <title>Términos y Condiciones - {COMPANY_NAME}</title>
    <style>
        body {{ font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif; line-height: 1.5; color: #333; max-width: 900px; margin: 0 auto; padding: 40px; background-color: #fff; font-size: 13px; }}
        h1 {{ font-size: 20px; font-weight: 700; margin-bottom: 20px; border-bottom: 1px solid #eee; padding-bottom: 10px; color: #000; }}
        h2 {{ font-size: 15px; font-weight: 700; margin-top: 30px; margin-bottom: 10px; color: #000; text-transform: uppercase; letter-spacing: 0.5px; }}
        h3 {{ font-size: 13px; font-weight: 700; margin-top: 20px; margin-bottom: 8px; }}
        p {{ margin-bottom: 12px; text-align: justify; }}
        ul {{ padding-left: 20px; margin-bottom: 12px; }}
        li {{ margin-bottom: 6px; }}
        table {{ width: 100%; border-collapse: collapse; margin: 20px 0; font-size: 12px; border: 1px solid #eee; }}
        th {{ border-bottom: 2px solid #333; text-align: left; padding: 10px; font-weight: 700; background-color: #f9f9f9; }}
        td {{ border-bottom: 1px solid #eee; padding: 10px; vertical-align: top; }}
        tr:last-child td {{ border-bottom: none; }}
        .header-info {{ font-size: 11px; color: #666; margin-bottom: 30px; border: 1px solid #eee; padding: 15px; background: #fcfcfc; }}
        a {{ color: #000; text-decoration: underline; }}
    </style>
</head>
<body>
    <h1>Términos y Condiciones de Uso</h1>
    
    <div class='header-info'>
        <p style='margin:0;'><strong>Titular:</strong> {COMPANY_NAME}</p>
        <p style='margin:0;'><strong>Sitio Web:</strong> {COMPANY_WEBSITE}</p>
        <p style='margin:0;'><strong>Contacto:</strong> {COMPANY_EMAIL}</p>
        <p style='margin:0;'><strong>Última actualización:</strong> {DateTime.Now:dd/MM/yyyy}</p>
    </div>

    <h2>1. Objeto y Aceptación</h2>
    <p>Los presentes Términos y Condiciones regulan el uso de la plataforma de intermediación {COMPANY_NAME}. El acceso y uso de la Plataforma implica la aceptación plena y sin reservas de estas condiciones.</p>

    <h2>2. Descripción del Servicio</h2>
    <p>{COMPANY_NAME} actúa como intermediario tecnológico conectando clientes con expertos independientes para servicios de inspección técnica. No prestamos directamente los servicios finales.</p>

    <h2>3. Política Económica y Comisiones</h2>
    <p>La contratación de servicios a través de la plataforma conlleva los siguientes costes y comisiones:</p>

    <h3>3.1. Comisión de Servicio (Éxito)</h3>
    <p>En servicios completados satisfactoriamente:</p>
    <ul>
        <li><strong>Comisión de Plataforma:</strong> {platformFee}% del total (IVA incluido).</li>
        <li><strong>Honorarios del Experto:</strong> {100 - platformFee}% del total.</li>
    </ul>

    <h3>3.2. Tabla General de Distribución de Fondos</h3>
    <p>A continuación se detallan los porcentajes aplicables a cada escenario posible registrado en nuestro sistema. Estos valores se actualizan automáticamente según la configuración vigente.</p>

    <table>
        <thead>
            <tr>
                <th style='width: 40%'>Escenario / Estado</th>
                <th style='width: 20%'>Reembolso Cliente</th>
                <th style='width: 20%'>Pago al Experto</th>
                <th style='width: 20%'>Comisión Plataforma</th>
            </tr>
        </thead>
        <tbody>
            {tableRows}
        </tbody>
    </table>
    <p><em>* En caso de reembolso parcial, la diferencia retenida cubre gastos de gestión administrativa, comisiones bancarias y compensación por bloqueo de agenda del experto.</em></p>

    <h2>4. Pagos y Facturación</h2>
    <p>Los pagos se procesan mediante Stripe. Los fondos se retienen hasta la finalización del servicio. La factura se emite automáticamente al Cliente una vez completado el servicio o aplicada la penalización correspondiente.</p>

    <h2>5. Responsabilidad</h2>
    <p>{COMPANY_NAME} verifica la identidad de los Expertos pero no garantiza el resultado técnico de las inspecciones. La responsabilidad profesional recae sobre el Experto contratado.</p>

    <h2>6. Legislación y Fuero</h2>
    <p>Se aplica la legislación española. Para cualquier litigio, las partes se someten a los tribunales del domicilio del titular de la web.</p>
</body>
</html>";

                return Ok(new 
                { 
                    content = htmlContent,
                    version = DateTime.UtcNow.ToString("yyyyMMdd"),
                    variables = new 
                    {
                        platformFee,
                        generatedCases = orderedConfigs.Count
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error generando términos", error = ex.Message });
            }
        }

        /// <summary>
        /// Obtiene la Política de Privacidad conforme al RGPD y LOPD-GDD (Estilo Profesional).
        /// </summary>
        [HttpGet("privacy")]
        public IActionResult GetPrivacyPolicy()
        {
            var year = DateTime.UtcNow.Year;

            var htmlContent = $@"
<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='UTF-8'>
    <title>Política de Privacidad - {COMPANY_NAME}</title>
    <style>
        body {{ font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif; line-height: 1.5; color: #333; max-width: 800px; margin: 0 auto; padding: 40px; background-color: #fff; font-size: 13px; }}
        h1 {{ font-size: 20px; font-weight: 700; margin-bottom: 20px; border-bottom: 1px solid #eee; padding-bottom: 10px; color: #000; }}
        h2 {{ font-size: 15px; font-weight: 700; margin-top: 30px; margin-bottom: 10px; color: #000; text-transform: uppercase; letter-spacing: 0.5px; }}
        h3 {{ font-size: 13px; font-weight: 700; margin-top: 20px; margin-bottom: 8px; }}
        p {{ margin-bottom: 12px; text-align: justify; }}
        ul {{ padding-left: 20px; margin-bottom: 12px; }}
        li {{ margin-bottom: 6px; }}
        table {{ width: 100%; border-collapse: collapse; margin: 20px 0; font-size: 12px; border: 1px solid #eee; }}
        th {{ border-bottom: 2px solid #333; text-align: left; padding: 10px; font-weight: 700; background-color: #f9f9f9; }}
        td {{ border-bottom: 1px solid #eee; padding: 10px; vertical-align: top; }}
        tr:last-child td {{ border-bottom: none; }}
        .header-info {{ font-size: 11px; color: #666; margin-bottom: 30px; border: 1px solid #eee; padding: 15px; background: #fcfcfc; }}
        a {{ color: #000; text-decoration: underline; }}
    </style>
</head>
<body>
    <h1>Política de Privacidad</h1>
    
    <div class='header-info'>
        <p style='margin:0;'><strong>Responsable:</strong> {COMPANY_NAME}</p>
        <p style='margin:0;'><strong>Sitio Web:</strong> {COMPANY_WEBSITE}</p>
        <p style='margin:0;'><strong>Contacto DPO:</strong> {COMPANY_SUPPORT_EMAIL}</p>
        <p style='margin:0;'><strong>Actualización:</strong> {DateTime.Now:dd/MM/yyyy}</p>
    </div>

    <p>En cumplimiento del Reglamento (UE) 2016/679 (RGPD) y la Ley Orgánica 3/2018 (LOPD-GDD), informamos sobre el tratamiento de sus datos personales.</p>

    <h2>1. Datos Recopilados</h2>
    <p>Recopilamos los datos necesarios para la prestación del servicio:</p>
    <ul>
        <li><strong>Identificación:</strong> Nombre, apellidos, DNI/NIF.</li>
        <li><strong>Contacto:</strong> Email, teléfono, dirección postal.</li>
        <li><strong>Transaccionales:</strong> Historial de pedidos, facturas (datos bancarios procesados externamente por Stripe).</li>
        <li><strong>Técnicos:</strong> Dirección IP, logs de acceso para seguridad.</li>
    </ul>

    <h2>2. Finalidad y Legitimación</h2>
    <table>
        <thead>
            <tr>
                <th>Finalidad</th>
                <th>Base Legal (Art. 6 RGPD)</th>
            </tr>
        </thead>
        <tbody>
            <tr>
                <td>Gestión del registro y cuenta de usuario</td>
                <td>Ejecución de contrato</td>
            </tr>
            <tr>
                <td>Intermediación y gestión de citas</td>
                <td>Ejecución de contrato</td>
            </tr>
            <tr>
                <td>Facturación y gestión de pagos</td>
                <td>Obligación legal / Contrato</td>
            </tr>
            <tr>
                <td>Seguridad y prevención de fraude</td>
                <td>Interés legítimo</td>
            </tr>
            <tr>
                <td>Comunicaciones comerciales (si autorizadas)</td>
                <td>Consentimiento</td>
            </tr>
        </tbody>
    </table>

    <h2>3. Destinatarios de los Datos</h2>
    <p>Sus datos se comunicarán únicamente a:</p>
    <ul>
        <li><strong>Expertos contratados:</strong> Datos estrictamente necesarios para la ejecución del servicio (ubicación, contacto básico).</li>
        <li><strong>Pasarela de pagos (Stripe):</strong> Para la ejecución de cobros y pagos.</li>
        <li><strong>Administración Pública:</strong> Para el cumplimiento de obligaciones fiscales y legales.</li>
        <li><strong>Proveedores tecnológicos:</strong> Encargados de tratamiento (hosting, email) bajo contrato de confidencialidad.</li>
    </ul>

    <h2>4. Conservación</h2>
    <p>Los datos se conservarán mientras se mantenga la relación contractual. Tras finalizarla, se mantendrán bloqueados durante los plazos de prescripción legal (generalmente 5 años para acciones civiles y 4 años para fiscales).</p>

    <h2>5. Derechos del Interesado</h2>
    <p>Usted puede ejercer sus derechos de acceso, rectificación, supresión, limitación, portabilidad y oposición enviando una solicitud a {COMPANY_SUPPORT_EMAIL}. Tiene derecho a presentar una reclamación ante la Agencia Española de Protección de Datos (AEPD) si considera vulnerados sus derechos.</p>

    <h2>6. Seguridad</h2>
    <p>Aplicamos medidas técnicas y organizativas (cifrado, protocolos seguros) para proteger sus datos contra acceso no autorizado, pérdida o alteración.</p>
</body>
</html>";

            return Ok(new 
            { 
                content = htmlContent,
                version = DateTime.UtcNow.ToString("yyyyMMdd"),
                companyName = COMPANY_NAME
            });
        }

        /// <summary>
        /// Obtiene el Aviso Legal conforme a la LSSI-CE (Estilo Profesional).
        /// </summary>
        [HttpGet("legal-notice")]
        public IActionResult GetLegalNotice()
        {
            var year = DateTime.UtcNow.Year;

            var htmlContent = $@"
<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='UTF-8'>
    <title>Aviso Legal - {COMPANY_NAME}</title>
    <style>
        body {{ font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif; line-height: 1.5; color: #333; max-width: 800px; margin: 0 auto; padding: 40px; background-color: #fff; font-size: 13px; }}
        h1 {{ font-size: 20px; font-weight: 700; margin-bottom: 20px; border-bottom: 1px solid #eee; padding-bottom: 10px; color: #000; }}
        h2 {{ font-size: 15px; font-weight: 700; margin-top: 30px; margin-bottom: 10px; color: #000; text-transform: uppercase; letter-spacing: 0.5px; }}
        p {{ margin-bottom: 12px; text-align: justify; }}
        ul {{ padding-left: 20px; margin-bottom: 12px; }}
        li {{ margin-bottom: 6px; }}
        .header-info {{ font-size: 11px; color: #666; margin-bottom: 30px; border: 1px solid #eee; padding: 15px; background: #fcfcfc; }}
        a {{ color: #000; text-decoration: underline; }}
    </style>
</head>
<body>
    <h1>Aviso Legal</h1>
    
    <div class='header-info'>
        <p style='margin:0;'><strong>Denominación Social:</strong> {COMPANY_NAME}</p>
        <p style='margin:0;'><strong>Dominio:</strong> {COMPANY_WEBSITE}</p>
        <p style='margin:0;'><strong>Email:</strong> {COMPANY_EMAIL}</p>
    </div>

    <h2>1. Objeto</h2>
    <p>El presente aviso legal regula el uso del sitio web {COMPANY_WEBSITE}, cumpliendo con la Ley 34/2002 de Servicios de la Sociedad de la Información y de Comercio Electrónico (LSSI-CE).</p>

    <h2>2. Propiedad Intelectual</h2>
    <p>Todos los contenidos del sitio web (código, diseño, marcas, textos) son propiedad intelectual de {COMPANY_NAME} o de terceros que han autorizado su uso. Queda prohibida su reproducción o explotación sin autorización expresa.</p>

    <h2>3. Responsabilidad</h2>
    <p>{COMPANY_NAME} no se hace responsable de los daños derivados del uso incorrecto del sitio web, interrupciones del servicio por causas técnicas o contenidos de enlaces externos.</p>

    <h2>4. Legislación</h2>
    <p>El presente Aviso Legal se rige por la legislación española. Cualquier controversia se someterá a los Juzgados y Tribunales competentes.</p>
</body>
</html>";

            return Ok(new 
            { 
                content = htmlContent,
                version = DateTime.UtcNow.ToString("yyyyMMdd"),
                companyName = COMPANY_NAME
            });
        }

        /// <summary>
        /// Helper para buscar configuración activa de un estado
        /// </summary>
        private async Task<StatusConfiguration?> GetConfigForStatus(string statusValue)
        {
            var status = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue);

            if (status == null) return null;

            return await _context.StatusConfigurations
                .Where(sc => sc.StatusId == status.Id && 
                             sc.IsActive && 
                             sc.CategoryId == null && 
                             sc.ServiceTypeCategoryId == null)
                .OrderByDescending(sc => sc.UpdatedAt)
                .FirstOrDefaultAsync();
        }
    }
}

