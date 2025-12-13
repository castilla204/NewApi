using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LegalController : ControllerBase
    {
        private readonly AppDbContext _context;

        public LegalController(AppDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Obtiene los Términos y Condiciones actualizados dinámicamente con las tarifas vigentes.
        /// </summary>
        [HttpGet("terms")]
        public async Task<IActionResult> GetTermsAndConditions()
        {
            try
            {
                // 1. Obtener configuración de éxito (Servicio Completado)
                // Usamos 'appointment_completed' o un fallback si no existe
                var successConfig = await GetConfigForStatus("appointment_completed") 
                                    ?? await GetConfigForStatus("appointment_confirmed"); // Fallback

                // Por defecto: Plataforma 20%, Experto 80%
                decimal platformFee = 20;
                if (successConfig != null)
                {
                    platformFee = successConfig.PlatformPercentage;
                }

                // 2. Obtener configuración de cancelación por cliente
                var cancelClientConfig = await GetConfigForStatus("appointment_cancelled_by_client");
                decimal clientRefundOnCancel = cancelClientConfig?.ClientPercentage ?? 0; // Por defecto 0% reembolso si cancela (estricto)

                // 3. Obtener configuración de cancelación por experto
                var cancelExpertConfig = await GetConfigForStatus("appointment_cancelled_by_expert");
                decimal clientRefundOnExpertCancel = cancelExpertConfig?.ClientPercentage ?? 100; // Por defecto 100% reembolso

                // Generar HTML
                var htmlContent = $@"
<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: 'Segoe UI', sans-serif; line-height: 1.6; color: #333; }}
        h1 {{ color: #2d3748; border-bottom: 2px solid #e2e8f0; padding-bottom: 10px; }}
        h2 {{ color: #4a5568; margin-top: 30px; }}
        .highlight {{ color: #4F46E5; font-weight: bold; }}
        ul {{ padding-left: 20px; }}
        li {{ margin-bottom: 10px; }}
    </style>
</head>
<body>
    <h1>Términos y Condiciones de Uso</h1>
    <p>Última actualización: {DateTime.Now:dd/MM/yyyy}</p>

    <h2>1. Introducción</h2>
    <p>Bienvenido a <strong>Inspecciono</strong>. Estos términos y condiciones rigen el uso de nuestra plataforma de intermediación entre clientes y expertos verificados.</p>

    <h2>2. Tarifas y Comisiones</h2>
    <p>Inspecciono cobra una comisión por servicio para mantener la plataforma, verificar expertos y procesar pagos seguros.</p>
    <ul>
        <li><strong>Comisión de Servicio:</strong> La plataforma retiene un <span class='highlight'>{platformFee}%</span> del valor total del servicio contratado.</li>
        <li><strong>Pago al Experto:</strong> El experto recibe el <span class='highlight'>{100 - platformFee}%</span> restante (menos impuestos aplicables).</li>
    </ul>

    <h2>3. Política de Cancelación y Reembolsos</h2>
    <p>Nuestras políticas están diseñadas para proteger tanto el tiempo de los expertos como el dinero de los clientes.</p>
    
    <h3>3.1 Cancelación por parte del Cliente</h3>
    <p>Si usted, como cliente, decide cancelar una cita confirmada:</p>
    <ul>
        <li>Se le reembolsará el <span class='highlight'>{clientRefundOnCancel}%</span> del importe pagado.</li>
        <li>La plataforma retendrá la diferencia como gastos de gestión y compensación por bloqueo de agenda.</li>
    </ul>

    <h3>3.2 Cancelación por parte del Experto</h3>
    <p>Si el experto cancela la cita o no se presenta:</p>
    <ul>
        <li>Usted recibirá un reembolso del <span class='highlight'>{clientRefundOnExpertCancel}%</span> (totalidad) de su dinero.</li>
        <li>El experto podría ser penalizado en su visibilidad o acceso a la plataforma.</li>
    </ul>

    <h2>4. Pagos y Fiscalidad</h2>
    <p>Todos los pagos se procesan de forma segura a través de <strong>Stripe</strong>.</p>
    <ul>
        <li>Los precios mostrados incluyen el IVA aplicable según la normativa vigente (generalmente 21% en España).</li>
        <li>Inspecciono actúa como intermediario de cobro.</li>
    </ul>

    <h2>5. Responsabilidades</h2>
    <p>Inspecciono valida la identidad de los expertos pero no se hace responsable del resultado final de las inspecciones técnicas, las cuales son responsabilidad exclusiva del profesional contratado.</p>
</body>
</html>";

                return Ok(new 
                { 
                    content = htmlContent,
                    version = DateTime.UtcNow.ToString("yyyyMMdd"),
                    variables = new 
                    {
                        platformFee,
                        clientRefundOnCancel,
                        clientRefundOnExpertCancel
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error generando términos", error = ex.Message });
            }
        }

        /// <summary>
        /// Obtiene la Política de Privacidad actualizada.
        /// </summary>
        [HttpGet("privacy")]
        public IActionResult GetPrivacyPolicy()
        {
            var htmlContent = $@"
<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: 'Segoe UI', sans-serif; line-height: 1.6; color: #333; }}
        h1 {{ color: #2d3748; border-bottom: 2px solid #e2e8f0; padding-bottom: 10px; }}
        h2 {{ color: #4a5568; margin-top: 30px; }}
    </style>
</head>
<body>
    <h1>Política de Privacidad</h1>
    <p>En Inspecciono, nos tomamos muy en serio la protección de sus datos personales.</p>

    <h2>1. Datos que Recopilamos</h2>
    <p>Para prestar nuestros servicios, recopilamos:</p>
    <ul>
        <li>Información de contacto (Nombre, Email, Teléfono).</li>
        <li>Datos de facturación y pago (procesados externamente por Stripe).</li>
        <li>Detalles de las citas y ubicaciones de inspección.</li>
    </ul>

    <h2>2. Uso de la Información</h2>
    <p>Utilizamos sus datos exclusivamente para:</p>
    <ul>
        <li>Gestionar las reservas de citas y pagos.</li>
        <li>Enviar notificaciones sobre el estado del servicio (Email, SMS).</li>
        <li>Cumplir con obligaciones legales y fiscales.</li>
    </ul>

    <h2>3. Compartir Datos</h2>
    <p>No vendemos sus datos. Solo compartimos la información necesaria con:</p>
    <ul>
        <li><strong>Expertos:</strong> Solo datos necesarios para realizar el servicio (ubicación, fecha).</li>
        <li><strong>Stripe:</strong> Para el procesamiento seguro de pagos.</li>
        <li><strong>Autoridades:</strong> Si somos requeridos legalmente.</li>
    </ul>

    <h2>4. Sus Derechos</h2>
    <p>Usted tiene derecho a acceder, rectificar o eliminar sus datos en cualquier momento desde su perfil de usuario o contactando a soporte.</p>
</body>
</html>";

            return Ok(new 
            { 
                content = htmlContent,
                version = "1.0"
            });
        }

        // Helper para buscar configuración activa
        private async Task<StatusConfiguration?> GetConfigForStatus(string statusValue)
        {
            // Buscamos el estado por su valor
            var status = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue);

            if (status == null) return null;

            // Buscamos la configuración activa por defecto (sin categoría específica)
            // Priorizamos la más general para los T&C
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

