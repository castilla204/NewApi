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

                // 2. Cancelación por Cliente (Estándar/Tardía)
                var cancelClientSecond = await GetConfigForStatus("appointment_cancelled_by_client_second");
                decimal refundClientSecond = cancelClientSecond?.ClientPercentage ?? 80;

                // 3. Cancelación por Cliente (Falta de Propuesta)
                var cancelClientNoProp = await GetConfigForStatus("appointment_cancelled_by_client_no_proposal");
                decimal refundClientNoProp = cancelClientNoProp?.ClientPercentage ?? 90;

                // 4. Cancelación por Experto
                var cancelExpertConfig = await GetConfigForStatus("appointment_cancelled_by_expert");
                decimal refundExpertCancel = cancelExpertConfig?.ClientPercentage ?? 100;

                var year = DateTime.UtcNow.Year;

                var htmlContent = $@"
<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='UTF-8'>
    <title>Términos y Condiciones - {COMPANY_NAME}</title>
    <style>
        body {{ font-family: 'Segoe UI', -apple-system, sans-serif; line-height: 1.7; color: #1a202c; max-width: 900px; margin: 0 auto; padding: 40px 20px; background: #f7fafc; }}
        .container {{ background: white; padding: 50px; border-radius: 12px; box-shadow: 0 4px 6px rgba(0,0,0,0.05); }}
        h1 {{ color: #1a202c; border-bottom: 3px solid #4F46E5; padding-bottom: 15px; font-size: 2em; }}
        h2 {{ color: #2d3748; margin-top: 40px; padding-bottom: 10px; border-bottom: 1px solid #e2e8f0; }}
        h3 {{ color: #4a5568; margin-top: 25px; }}
        .highlight {{ color: #4F46E5; font-weight: 700; background: linear-gradient(120deg, #EEF2FF 0%, #E0E7FF 100%); padding: 3px 8px; border-radius: 4px; }}
        .company-info {{ background: #f0fdf4; border: 1px solid #86efac; padding: 20px; border-radius: 8px; margin: 20px 0; }}
        .warning-box {{ background: #fef3c7; border-left: 4px solid #f59e0b; padding: 15px 20px; margin: 20px 0; border-radius: 0 8px 8px 0; }}
        table {{ width: 100%; border-collapse: collapse; margin: 25px 0; border-radius: 8px; overflow: hidden; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }}
        th {{ background: #4F46E5; color: white; text-align: left; padding: 15px; font-weight: 600; }}
        td {{ padding: 15px; border-bottom: 1px solid #e2e8f0; }}
        tr:nth-child(even) {{ background: #f8fafc; }}
        tr:last-child td {{ border-bottom: none; }}
        .text-green {{ color: #059669; font-weight: 700; }}
        .text-red {{ color: #dc2626; font-weight: 700; }}
        .text-muted {{ color: #64748b; font-size: 0.9em; }}
        ul {{ padding-left: 25px; }}
        li {{ margin-bottom: 12px; }}
        .update-date {{ color: #64748b; font-size: 0.95em; margin-bottom: 30px; }}
        a {{ color: #4F46E5; text-decoration: none; }}
        a:hover {{ text-decoration: underline; }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>📜 Términos y Condiciones de Uso</h1>
        <p class='update-date'>Última actualización: {DateTime.Now:dd} de {DateTime.Now:MMMM} de {year}</p>

        <div class='company-info'>
            <strong>🏢 Información del Titular:</strong><br>
            Sitio web: <a href='https://{COMPANY_WEBSITE}'>{COMPANY_WEBSITE}</a><br>
            Contacto: <a href='mailto:{COMPANY_EMAIL}'>{COMPANY_EMAIL}</a>
        </div>

        <h2>1. Objeto y Ámbito de Aplicación</h2>
        <p>Los presentes Términos y Condiciones regulan el acceso y uso del sitio web <strong><a href='https://{COMPANY_WEBSITE}'>{COMPANY_WEBSITE}</a></strong> (en adelante, ""la Plataforma""), propiedad de <strong>{COMPANY_NAME}</strong>.</p>
        <p>{COMPANY_NAME} es una plataforma de intermediación que conecta a <strong>Clientes</strong> con <strong>Expertos verificados</strong> para la prestación de servicios de inspección técnica. Al acceder o utilizar la Plataforma, usted acepta quedar vinculado por estos términos.</p>

        <h2>2. Descripción del Servicio</h2>
        <p>{COMPANY_NAME} actúa exclusivamente como <strong>intermediario</strong> entre Clientes y Expertos. Nuestros servicios incluyen:</p>
        <ul>
            <li>Verificación de identidad y credenciales de los Expertos.</li>
            <li>Gestión de reservas y citas entre las partes.</li>
            <li>Procesamiento seguro de pagos a través de pasarelas certificadas (Stripe).</li>
            <li>Sistema de valoraciones y reseñas.</li>
            <li>Soporte al cliente.</li>
        </ul>

        <h2>3. Tarifas y Comisiones</h2>
        <p>Por la prestación de nuestros servicios de intermediación, {COMPANY_NAME} aplica las siguientes tarifas:</p>
        
        <table>
            <thead>
                <tr>
                    <th>Concepto</th>
                    <th>Porcentaje</th>
                    <th>Descripción</th>
                </tr>
            </thead>
            <tbody>
                <tr>
                    <td><strong>Comisión de Plataforma</strong></td>
                    <td><span class='highlight'>{platformFee}%</span></td>
                    <td>Se deduce del pago total al completarse el servicio.</td>
                </tr>
                <tr>
                    <td><strong>Pago al Experto</strong></td>
                    <td><span class='highlight'>{100 - platformFee}%</span></td>
                    <td>El experto recibe esta parte tras completar el servicio.</td>
                </tr>
            </tbody>
        </table>
        <p class='text-muted'>Los precios mostrados en la plataforma incluyen el IVA aplicable (21% en España).</p>

        <h2>4. Política de Cancelaciones y Reembolsos</h2>
        <p>Entendemos que los planes pueden cambiar. A continuación se detallan las condiciones de reembolso según el escenario:</p>
        
        <table>
            <thead>
                <tr>
                    <th>Escenario</th>
                    <th>Reembolso</th>
                    <th>Penalización</th>
                </tr>
            </thead>
            <tbody>
                <tr>
                    <td>
                        <strong>Cancelación por el Experto</strong><br>
                        <span class='text-muted'>El experto no puede asistir, rechaza o no responde.</span>
                    </td>
                    <td class='text-green'>{refundExpertCancel}% (Total)</td>
                    <td>Sin coste para el cliente</td>
                </tr>
                <tr>
                    <td>
                        <strong>Cliente no propone cita a tiempo</strong><br>
                        <span class='text-muted'>El cliente no responde en el plazo establecido (24-48h).</span>
                    </td>
                    <td><span class='highlight'>{refundClientNoProp}%</span></td>
                    <td class='text-red'>{100 - refundClientNoProp}%</td>
                </tr>
                <tr>
                    <td>
                        <strong>Cancelación tardía por Cliente</strong><br>
                        <span class='text-muted'>Cancelación una vez confirmada la cita.</span>
                    </td>
                    <td><span class='highlight'>{refundClientSecond}%</span></td>
                    <td class='text-red'>{100 - refundClientSecond}%</td>
                </tr>
            </tbody>
        </table>

        <div class='warning-box'>
            <strong>⚠️ Importante:</strong> Las penalizaciones se aplican para compensar al Experto por el tiempo reservado en su agenda y cubrir los gastos de gestión de la plataforma. Los reembolsos se procesan en un plazo de 5-10 días hábiles.
        </div>

        <h2>5. Procesamiento de Pagos</h2>
        <p>Todos los pagos se procesan de forma segura a través de <strong>Stripe</strong>, una pasarela de pago certificada PCI-DSS Nivel 1.</p>
        <ul>
            <li>{COMPANY_NAME} no almacena datos de tarjetas de crédito/débito.</li>
            <li>Los fondos se retienen hasta la finalización satisfactoria del servicio.</li>
            <li>Se aceptan las principales tarjetas de crédito y débito (Visa, Mastercard, etc.).</li>
        </ul>

        <h2>6. Obligaciones del Usuario</h2>
        <p>Al utilizar la Plataforma, usted se compromete a:</p>
        <ul>
            <li>Proporcionar información veraz y actualizada.</li>
            <li>No utilizar la Plataforma para fines ilícitos o fraudulentos.</li>
            <li>Respetar los derechos de los demás usuarios y Expertos.</li>
            <li>No eludir el sistema de pagos de la Plataforma.</li>
            <li>Comunicarse de forma respetuosa a través de los canales proporcionados.</li>
        </ul>

        <h2>7. Propiedad Intelectual</h2>
        <p>Todo el contenido de <strong>{COMPANY_WEBSITE}</strong>, incluyendo pero no limitado a textos, gráficos, logotipos, iconos, imágenes, software y diseño, es propiedad de {COMPANY_NAME} o de sus licenciantes y está protegido por las leyes de propiedad intelectual e industrial.</p>
        <p>Queda prohibida la reproducción, distribución, comunicación pública o transformación de dicho contenido sin autorización expresa por escrito.</p>

        <h2>8. Limitación de Responsabilidad</h2>
        <p>{COMPANY_NAME} actúa como intermediario y:</p>
        <ul>
            <li><strong>No garantiza</strong> el resultado final de las inspecciones técnicas realizadas por los Expertos.</li>
            <li><strong>No se hace responsable</strong> de daños derivados de la actuación de los Expertos.</li>
            <li><strong>No asume responsabilidad</strong> por interrupciones del servicio por causas ajenas a su control.</li>
        </ul>
        <p>La responsabilidad máxima de {COMPANY_NAME} se limitará, en todo caso, al importe pagado por el servicio contratado.</p>

        <h2>9. Modificaciones</h2>
        <p>{COMPANY_NAME} se reserva el derecho de modificar estos Términos y Condiciones en cualquier momento. Las modificaciones entrarán en vigor desde su publicación en la Plataforma. Se notificará a los usuarios registrados de cambios sustanciales por correo electrónico.</p>

        <h2>10. Legislación Aplicable y Jurisdicción</h2>
        <p>Estos Términos se rigen por la legislación española. Para la resolución de cualquier controversia, las partes se someten a los Juzgados y Tribunales de la ciudad de domicilio del usuario, salvo que la ley establezca otro fuero.</p>

        <h2>11. Contacto</h2>
        <p>Para cualquier consulta sobre estos términos:</p>
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
                    variables = new 
                    {
                        platformFee,
                        refundClientSecond,
                        refundClientNoProp,
                        refundExpertCancel,
                        companyName = COMPANY_NAME,
                        website = COMPANY_WEBSITE
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

