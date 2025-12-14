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
                // 1. Éxito (Servicio Completado)
                var successConfig = await GetConfigForStatus("completed") 
                                    ?? await GetConfigForStatus("appointment_completed_without_client_approval");
                decimal platformFee = successConfig?.PlatformPercentage ?? 5;

                // 2. Cancelación por Cliente (Estándar/Tardía - Segunda Instancia)
                var cancelClientSecond = await GetConfigForStatus("appointment_cancelled_by_client_second");
                decimal refundClientSecond = cancelClientSecond?.ClientPercentage ?? 80;

                // 3. Cancelación por Cliente (Falta de Propuesta)
                var cancelClientNoProp = await GetConfigForStatus("appointment_cancelled_by_client_no_proposal");
                decimal refundClientNoProp = cancelClientNoProp?.ClientPercentage ?? 90;

                // 4. Cancelación por Experto (General)
                var cancelExpertConfig = await GetConfigForStatus("appointment_cancelled_by_expert");
                decimal refundExpertCancel = cancelExpertConfig?.ClientPercentage ?? 100;

                // 5. Cancelación por Experto (No Respuesta)
                var cancelExpertNoResp = await GetConfigForStatus("appointment_cancelled_by_expert_no_response");
                decimal refundExpertNoResp = cancelExpertNoResp?.ClientPercentage ?? 100;

                var year = DateTime.UtcNow.Year;

                var htmlContent = $@"
<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='UTF-8'>
    <title>Términos y Condiciones - {COMPANY_NAME}</title>
    <style>
        body {{ font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif; line-height: 1.5; color: #333; max-width: 800px; margin: 0 auto; padding: 40px; background-color: #fff; font-size: 13px; }}
        h1 {{ font-size: 20px; font-weight: 700; margin-bottom: 20px; border-bottom: 1px solid #eee; padding-bottom: 10px; color: #000; }}
        h2 {{ font-size: 15px; font-weight: 700; margin-top: 30px; margin-bottom: 10px; color: #000; text-transform: uppercase; letter-spacing: 0.5px; }}
        h3 {{ font-size: 13px; font-weight: 700; margin-top: 20px; margin-bottom: 8px; }}
        p {{ margin-bottom: 12px; text-align: justify; }}
        ul {{ padding-left: 20px; margin-bottom: 12px; }}
        li {{ margin-bottom: 6px; }}
        table {{ width: 100%; border-collapse: collapse; margin: 20px 0; font-size: 12px; }}
        th {{ border-bottom: 2px solid #333; text-align: left; padding: 8px; font-weight: 700; }}
        td {{ border-bottom: 1px solid #eee; padding: 8px; vertical-align: top; }}
        .header-info {{ font-size: 11px; color: #666; margin-bottom: 30px; border: 1px solid #eee; padding: 15px; background: #fcfcfc; }}
        .strong {{ font-weight: 600; }}
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
    <p>Los presentes Términos y Condiciones regulan el uso de la plataforma de intermediación {COMPANY_NAME} (en adelante, ""la Plataforma""). El acceso y uso de la Plataforma implica la aceptación plena y sin reservas de todas y cada una de las condiciones incluidas en este documento.</p>

    <h2>2. Descripción del Servicio</h2>
    <p>{COMPANY_NAME} pone a disposición de los usuarios una herramienta tecnológica para conectar a clientes demandantes de servicios de inspección técnica con profesionales independientes (""Expertos""). {COMPANY_NAME} actúa únicamente como intermediario, no prestando directamente los servicios de inspección.</p>

    <h2>3. Política Económica y Tarifas</h2>
    <p>El uso de la Plataforma conlleva la aplicación de tarifas de gestión y comisiones sobre los servicios contratados.</p>

    <h3>3.1. Comisión de Servicio</h3>
    <p>Por la intermediación y gestión de pagos, se aplica la siguiente estructura de comisiones sobre el precio total del servicio (IVA incluido):</p>
    <ul>
        <li><strong>Comisión de Plataforma:</strong> {platformFee}%</li>
        <li><strong>Honorarios del Experto:</strong> {100 - platformFee}%</li>
    </ul>

    <h3>3.2. Política de Cancelación y Reembolsos</h3>
    <p>La política de cancelación está diseñada para equilibrar los derechos del Cliente y del Experto. Los porcentajes de reembolso se calculan sobre el importe total pagado.</p>

    <table>
        <thead>
            <tr>
                <th>Escenario de Cancelación</th>
                <th>Reembolso al Cliente</th>
                <th>Cargo por Gestión/Penalización</th>
            </tr>
        </thead>
        <tbody>
            <tr>
                <td>Cancelación por el Experto (Cualquier motivo)</td>
                <td>{refundExpertCancel}%</td>
                <td>0%</td>
            </tr>
            <tr>
                <td>Cancelación por Cliente (Falta de respuesta/propuesta)</td>
                <td>{refundClientNoProp}%</td>
                <td>{100 - refundClientNoProp}%</td>
            </tr>
            <tr>
                <td>Cancelación por Cliente (Una vez confirmada/Tardía)</td>
                <td>{refundClientSecond}%</td>
                <td>{100 - refundClientSecond}%</td>
            </tr>
        </tbody>
    </table>
    <p><em>Nota: Los cargos por gestión cubren los costes operativos, comisiones bancarias y, en su caso, la compensación al Experto por el bloqueo de agenda.</em></p>

    <h2>4. Pagos y Facturación</h2>
    <p>Los pagos se procesan a través de la pasarela segura Stripe. El Cliente autoriza el cargo en su tarjeta en el momento de la solicitud. Los fondos permanecen retenidos hasta la finalización del servicio o la aplicación de una política de cancelación.</p>
    <p>La factura correspondiente al servicio será emitida y enviada automáticamente al correo electrónico del Cliente una vez completado el servicio.</p>

    <h2>5. Responsabilidad</h2>
    <p>{COMPANY_NAME} verifica la identidad de los Expertos pero no garantiza la exactitud, veracidad o exhaustividad de los informes técnicos emitidos, siendo estos responsabilidad exclusiva del Experto. {COMPANY_NAME} no será responsable de daños indirectos, lucro cesante o pérdidas de oportunidades de negocio.</p>

    <h2>6. Protección de Datos</h2>
    <p>El tratamiento de datos personales se rige por lo dispuesto en nuestra Política de Privacidad, cumpliendo con el Reglamento (UE) 2016/679 (RGPD) y la Ley Orgánica 3/2018 (LOPD-GDD).</p>

    <h2>7. Legislación y Fuero</h2>
    <p>Para la resolución de controversias, las partes se someten a los juzgados y tribunales del domicilio del Titular de la Plataforma, salvo que la ley imponga otro fuero.</p>
</body>
</html>";

                return Ok(new 
                { 
                    content = htmlContent,
                    version = DateTime.UtcNow.ToString("yyyyMMdd"),
                    variables = new 
                    {
                        platformFee,
                        refundClientSecond,
                        refundClientNoProp,
                        refundExpertCancel,
                        refundExpertNoResp
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error generando términos", error = ex.Message });
            }
        }

        /// <summary>
        /// Obtiene la Política de Privacidad conforme al RGPD y LOPD-GDD.
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
        body {{ font-family: 'Segoe UI', -apple-system, sans-serif; line-height: 1.7; color: #1a202c; max-width: 900px; margin: 0 auto; padding: 40px 20px; background: #f7fafc; }}
        .container {{ background: white; padding: 50px; border-radius: 12px; box-shadow: 0 4px 6px rgba(0,0,0,0.05); }}
        h1 {{ color: #1a202c; border-bottom: 3px solid #4F46E5; padding-bottom: 15px; font-size: 2em; }}
        h2 {{ color: #2d3748; margin-top: 40px; padding-bottom: 10px; border-bottom: 1px solid #e2e8f0; }}
        h3 {{ color: #4a5568; margin-top: 25px; }}
        .highlight {{ color: #4F46E5; font-weight: 700; }}
        .company-info {{ background: #eff6ff; border: 1px solid #93c5fd; padding: 20px; border-radius: 8px; margin: 20px 0; }}
        .rights-box {{ background: #f0fdf4; border: 1px solid #86efac; padding: 20px; border-radius: 8px; margin: 20px 0; }}
        table {{ width: 100%; border-collapse: collapse; margin: 25px 0; border-radius: 8px; overflow: hidden; }}
        th {{ background: #4F46E5; color: white; text-align: left; padding: 12px 15px; }}
        td {{ padding: 12px 15px; border-bottom: 1px solid #e2e8f0; vertical-align: top; }}
        tr:nth-child(even) {{ background: #f8fafc; }}
        ul {{ padding-left: 25px; }}
        li {{ margin-bottom: 12px; }}
        .update-date {{ color: #64748b; font-size: 0.95em; margin-bottom: 30px; }}
        a {{ color: #4F46E5; text-decoration: none; }}
        a:hover {{ text-decoration: underline; }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>🔒 Política de Privacidad</h1>
        <p class='update-date'>Última actualización: {DateTime.Now:dd} de {DateTime.Now:MMMM} de {year}</p>

        <p>En <strong>{COMPANY_NAME}</strong> (<a href='https://{COMPANY_WEBSITE}'>{COMPANY_WEBSITE}</a>), nos comprometemos a proteger la privacidad de nuestros usuarios. Esta política describe cómo recopilamos, usamos y protegemos su información personal conforme al <strong>Reglamento General de Protección de Datos (RGPD)</strong> y la <strong>Ley Orgánica 3/2018 de Protección de Datos (LOPD-GDD)</strong>.</p>

        <div class='company-info'>
            <strong>📋 Responsable del Tratamiento:</strong><br>
            Nombre: {COMPANY_NAME}<br>
            Sitio web: <a href='https://{COMPANY_WEBSITE}'>{COMPANY_WEBSITE}</a><br>
            Email de contacto: <a href='mailto:{COMPANY_EMAIL}'>{COMPANY_EMAIL}</a><br>
            Email DPO/Privacidad: <a href='mailto:{COMPANY_SUPPORT_EMAIL}'>{COMPANY_SUPPORT_EMAIL}</a>
        </div>

        <h2>1. Datos Personales que Recopilamos</h2>
        
        <h3>1.1 Datos proporcionados por el usuario:</h3>
        <ul>
            <li><strong>Datos de registro:</strong> Nombre, apellidos, correo electrónico, teléfono, contraseña.</li>
            <li><strong>Datos de perfil:</strong> Fotografía, dirección, preferencias.</li>
            <li><strong>Datos de pago:</strong> Procesados directamente por Stripe (no almacenamos números de tarjeta).</li>
            <li><strong>Comunicaciones:</strong> Mensajes enviados a través de la plataforma o soporte.</li>
        </ul>

        <h3>1.2 Datos recopilados automáticamente:</h3>
        <ul>
            <li><strong>Datos de navegación:</strong> Dirección IP, tipo de navegador, páginas visitadas.</li>
            <li><strong>Datos del dispositivo:</strong> Sistema operativo, identificadores únicos.</li>
            <li><strong>Cookies:</strong> Ver nuestra <a href='#cookies'>Política de Cookies</a>.</li>
        </ul>

        <h2>2. Finalidad del Tratamiento</h2>
        <table>
            <thead>
                <tr>
                    <th>Finalidad</th>
                    <th>Base Legal (RGPD)</th>
                </tr>
            </thead>
            <tbody>
                <tr>
                    <td>Gestión de cuentas de usuario y autenticación</td>
                    <td>Art. 6.1.b) Ejecución de contrato</td>
                </tr>
                <tr>
                    <td>Prestación del servicio de intermediación</td>
                    <td>Art. 6.1.b) Ejecución de contrato</td>
                </tr>
                <tr>
                    <td>Procesamiento de pagos y facturación</td>
                    <td>Art. 6.1.b) Ejecución de contrato</td>
                </tr>
                <tr>
                    <td>Envío de comunicaciones sobre el servicio</td>
                    <td>Art. 6.1.b) Ejecución de contrato</td>
                </tr>
                <tr>
                    <td>Envío de comunicaciones comerciales</td>
                    <td>Art. 6.1.a) Consentimiento</td>
                </tr>
                <tr>
                    <td>Mejora del servicio y análisis de uso</td>
                    <td>Art. 6.1.f) Interés legítimo</td>
                </tr>
                <tr>
                    <td>Cumplimiento de obligaciones legales y fiscales</td>
                    <td>Art. 6.1.c) Obligación legal</td>
                </tr>
            </tbody>
        </table>

        <h2>3. Destinatarios de los Datos</h2>
        <p>Sus datos pueden ser comunicados a:</p>
        <ul>
            <li><strong>Expertos:</strong> Únicamente los datos necesarios para prestar el servicio (nombre, ubicación de la cita, fecha).</li>
            <li><strong>Stripe:</strong> Para el procesamiento seguro de pagos. <a href='https://stripe.com/es/privacy' target='_blank'>Ver política de Stripe</a>.</li>
            <li><strong>Proveedores de servicios:</strong> Hosting, email (bajo contratos de encargado de tratamiento).</li>
            <li><strong>Autoridades públicas:</strong> Cuando exista obligación legal.</li>
        </ul>
        <p><strong>No vendemos ni compartimos sus datos con terceros para fines publicitarios.</strong></p>

        <h2>4. Transferencias Internacionales</h2>
        <p>Algunos de nuestros proveedores pueden estar ubicados fuera del Espacio Económico Europeo (EEE). En estos casos, garantizamos que las transferencias se realizan con las garantías adecuadas (Cláusulas Contractuales Tipo de la UE, decisiones de adecuación, etc.).</p>

        <h2>5. Conservación de Datos</h2>
        <ul>
            <li><strong>Datos de cuenta:</strong> Mientras mantenga su cuenta activa y 5 años después de la baja.</li>
            <li><strong>Datos de transacciones:</strong> 6 años (obligación fiscal).</li>
            <li><strong>Comunicaciones de soporte:</strong> 3 años.</li>
            <li><strong>Datos de navegación:</strong> 13 meses máximo.</li>
        </ul>

        <h2>6. Sus Derechos (RGPD)</h2>
        <div class='rights-box'>
            <p>Como usuario, usted tiene los siguientes derechos:</p>
            <ul>
                <li>✅ <strong>Acceso:</strong> Conocer qué datos tenemos sobre usted.</li>
                <li>✏️ <strong>Rectificación:</strong> Corregir datos inexactos o incompletos.</li>
                <li>🗑️ <strong>Supresión:</strong> Solicitar la eliminación de sus datos (""derecho al olvido"").</li>
                <li>⏸️ <strong>Limitación:</strong> Restringir el tratamiento en ciertos casos.</li>
                <li>📦 <strong>Portabilidad:</strong> Recibir sus datos en formato estructurado.</li>
                <li>🚫 <strong>Oposición:</strong> Oponerse al tratamiento, especialmente para marketing.</li>
                <li>🔄 <strong>Retirar consentimiento:</strong> En cualquier momento, sin efecto retroactivo.</li>
            </ul>
            <p><strong>Para ejercer sus derechos:</strong> Envíe un email a <a href='mailto:{COMPANY_SUPPORT_EMAIL}'>{COMPANY_SUPPORT_EMAIL}</a> adjuntando copia de su DNI.</p>
            <p><strong>Autoridad de control:</strong> Puede presentar reclamación ante la <a href='https://www.aepd.es' target='_blank'>Agencia Española de Protección de Datos (AEPD)</a>.</p>
        </div>

        <h2>7. Medidas de Seguridad</h2>
        <p>Implementamos medidas técnicas y organizativas para proteger sus datos:</p>
        <ul>
            <li>🔐 Cifrado de datos en tránsito (HTTPS/TLS).</li>
            <li>🔑 Contraseñas almacenadas con hash seguro (bcrypt).</li>
            <li>🛡️ Protección contra ataques (firewall, rate limiting).</li>
            <li>👥 Acceso restringido al personal autorizado.</li>
            <li>💳 Datos de pago procesados por Stripe (PCI-DSS Nivel 1).</li>
        </ul>

        <h2 id='cookies'>8. Política de Cookies</h2>
        <p>Utilizamos cookies para mejorar su experiencia. Tipos de cookies:</p>
        <table>
            <thead>
                <tr>
                    <th>Tipo</th>
                    <th>Finalidad</th>
                    <th>Duración</th>
                </tr>
            </thead>
            <tbody>
                <tr>
                    <td><strong>Esenciales</strong></td>
                    <td>Funcionamiento básico del sitio, autenticación</td>
                    <td>Sesión</td>
                </tr>
                <tr>
                    <td><strong>Analíticas</strong></td>
                    <td>Análisis de uso (Google Analytics)</td>
                    <td>13 meses</td>
                </tr>
                <tr>
                    <td><strong>Preferencias</strong></td>
                    <td>Recordar ajustes del usuario</td>
                    <td>1 año</td>
                </tr>
            </tbody>
        </table>
        <p>Puede gestionar sus preferencias de cookies en cualquier momento desde el banner de cookies o la configuración de su navegador.</p>

        <h2>9. Modificaciones</h2>
        <p>Nos reservamos el derecho de actualizar esta política. Notificaremos cambios significativos por email o mediante aviso en la plataforma.</p>

        <h2>10. Contacto</h2>
        <p>Para cualquier consulta sobre privacidad:</p>
        <ul>
            <li>📧 Email: <a href='mailto:{COMPANY_SUPPORT_EMAIL}'>{COMPANY_SUPPORT_EMAIL}</a></li>
            <li>🌐 Web: <a href='https://{COMPANY_WEBSITE}'>{COMPANY_WEBSITE}</a></li>
        </ul>

        <p style='margin-top: 40px; padding-top: 20px; border-top: 1px solid #e2e8f0; color: #64748b; font-size: 0.9em;'>
            © {year} {COMPANY_NAME}. Todos los derechos reservados.
        </p>
    </div>
</body>
</html>";

            return Ok(new 
            { 
                content = htmlContent,
                version = DateTime.UtcNow.ToString("yyyyMMdd"),
                companyName = COMPANY_NAME,
                website = COMPANY_WEBSITE
            });
        }

        /// <summary>
        /// Obtiene el Aviso Legal conforme a la LSSI-CE.
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
        body {{ font-family: 'Segoe UI', -apple-system, sans-serif; line-height: 1.7; color: #1a202c; max-width: 900px; margin: 0 auto; padding: 40px 20px; background: #f7fafc; }}
        .container {{ background: white; padding: 50px; border-radius: 12px; box-shadow: 0 4px 6px rgba(0,0,0,0.05); }}
        h1 {{ color: #1a202c; border-bottom: 3px solid #4F46E5; padding-bottom: 15px; }}
        h2 {{ color: #2d3748; margin-top: 35px; }}
        .company-info {{ background: #f8fafc; border: 1px solid #e2e8f0; padding: 25px; border-radius: 8px; margin: 25px 0; }}
        .company-info p {{ margin: 8px 0; }}
        ul {{ padding-left: 25px; }}
        li {{ margin-bottom: 10px; }}
        a {{ color: #4F46E5; text-decoration: none; }}
        a:hover {{ text-decoration: underline; }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>⚖️ Aviso Legal</h1>
        <p style='color: #64748b;'>En cumplimiento de la Ley 34/2002, de 11 de julio, de Servicios de la Sociedad de la Información y de Comercio Electrónico (LSSI-CE)</p>

        <div class='company-info'>
            <h3 style='margin-top: 0;'>Datos Identificativos del Titular</h3>
            <p><strong>Nombre comercial:</strong> {COMPANY_NAME}</p>
            <p><strong>Dominio:</strong> <a href='https://{COMPANY_WEBSITE}'>{COMPANY_WEBSITE}</a></p>
            <p><strong>Email de contacto:</strong> <a href='mailto:{COMPANY_EMAIL}'>{COMPANY_EMAIL}</a></p>
            <p><strong>Actividad:</strong> Plataforma de intermediación para servicios de inspección técnica</p>
        </div>

        <h2>1. Objeto</h2>
        <p>El presente Aviso Legal regula el acceso y uso del sitio web <strong>{COMPANY_WEBSITE}</strong>, del que es titular {COMPANY_NAME}.</p>
        <p>El acceso al sitio web es gratuito salvo en lo relativo al coste de la conexión a través de la red de telecomunicaciones suministrada por el proveedor de acceso contratado por el usuario.</p>

        <h2>2. Condiciones de Uso</h2>
        <p>El usuario se compromete a utilizar el sitio web y sus servicios conforme a la ley, la moral, el orden público y las presentes condiciones. Asimismo, se compromete a:</p>
        <ul>
            <li>No realizar actividades ilícitas o contrarias a la buena fe.</li>
            <li>No difundir contenidos de carácter racista, xenófobo, pornográfico o que atenten contra los derechos humanos.</li>
            <li>No provocar daños en los sistemas físicos y lógicos del sitio web.</li>
            <li>No introducir o difundir virus informáticos.</li>
            <li>No intentar acceder a áreas restringidas de los sistemas informáticos.</li>
        </ul>

        <h2>3. Propiedad Intelectual e Industrial</h2>
        <p>Todos los contenidos del sitio web (textos, fotografías, gráficos, imágenes, tecnología, software, diseño gráfico, código fuente, etc.) son propiedad intelectual de {COMPANY_NAME} o de terceros licenciantes, sin que puedan entenderse cedidos al usuario ninguno de los derechos de explotación.</p>
        <p>Las marcas, nombres comerciales y signos distintivos son propiedad de {COMPANY_NAME} o de terceros, no pudiendo considerarse que el acceso al sitio web atribuya derecho alguno sobre ellos.</p>

        <h2>4. Exclusión de Responsabilidad</h2>
        <p>{COMPANY_NAME} no se hace responsable de:</p>
        <ul>
            <li>Los daños que pudieran derivarse de interferencias, omisiones, interrupciones, virus informáticos, averías o desconexiones.</li>
            <li>Los retrasos o bloqueos causados por deficiencias de las líneas telefónicas o sobrecargas en Internet.</li>
            <li>Las acciones de terceros que vulneren los sistemas de seguridad.</li>
            <li>La imposibilidad de dar el servicio por causas no imputables.</li>
        </ul>

        <h2>5. Enlaces a Terceros</h2>
        <p>Este sitio web puede contener enlaces a páginas de terceros. {COMPANY_NAME} no asume responsabilidad por el contenido de dichas páginas ni por los daños que pudieran derivarse de su acceso.</p>

        <h2>6. Protección de Datos</h2>
        <p>El tratamiento de datos personales se rige por nuestra <a href='/api/Legal/privacy'>Política de Privacidad</a>.</p>

        <h2>7. Legislación Aplicable y Jurisdicción</h2>
        <p>Las relaciones entre {COMPANY_NAME} y el usuario se regirán por la legislación española. Para la resolución de cualquier controversia, las partes se someterán a los Juzgados y Tribunales correspondientes conforme a derecho.</p>

        <p style='margin-top: 40px; padding-top: 20px; border-top: 1px solid #e2e8f0; color: #64748b; font-size: 0.9em;'>
            © {year} {COMPANY_NAME}. Todos los derechos reservados.
        </p>
    </div>
</body>
</html>";

            return Ok(new 
            { 
                content = htmlContent,
                version = DateTime.UtcNow.ToString("yyyyMMdd"),
                companyName = COMPANY_NAME,
                website = COMPANY_WEBSITE
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

