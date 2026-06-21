using System.IO;

namespace newApi.Services
{
    /// <summary>
    /// Render único de la plantilla base de email (Resources/EmailTemplate.html).
    /// Fuente de verdad compartida por NotificationService, InvoiceService y el preview admin.
    /// El botón canónico es el bulletproof grande (50px/260px, radius 12px).
    /// </summary>
    public static class EmailTemplateRenderer
    {
        public static string GenerateEmailTemplate(string title, string content, string? actionText = null, string? actionUrl = null, string headerIcon = "📢")
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

            // La fila de cierre de la tarjeta SIEMPRE se emite aquí (redondea el fondo y fija el
            // inset inferior). Con CTA lleva el botón; sin CTA es una fila mínima — así no queda
            // una fila vacía con 32px muertos cuando el email no tiene botón (p.ej. OTP).
            string actionButtonHtml;
            if (!string.IsNullOrEmpty(actionText) && !string.IsNullOrEmpty(actionUrl))
            {
                actionButtonHtml = $@"
                    <tr>
                      <td class='card btn-row px' style='background-color:#FFFFFF; padding:0 40px 32px 40px; border-radius:0 0 8px 8px;'>
                        <table role='presentation' cellpadding='0' cellspacing='0' border='0' align='center' class='btn-td' style='margin:0 auto;'>
                            <tr>
                                <td align='center' bgcolor='#2563EB' style='border-radius:6px;'>
                                    <!--[if mso]>
                                    <v:roundrect xmlns:v='urn:schemas-microsoft-com:vml' xmlns:w='urn:schemas-microsoft-com:office:word' href='{actionUrl}' style='height:48px;v-text-anchor:middle;width:240px;' arcsize='13%' stroke='f' fillcolor='#2563EB'>
                                    <w:anchorlock/>
                                    <center style='color:#ffffff;font-family:Helvetica Neue,Helvetica,Arial,sans-serif;font-size:15px;font-weight:600;'>{actionText}</center>
                                    </v:roundrect>
                                    <![endif]-->
                                    <!--[if !mso]><!-- -->
                                    <a href='{actionUrl}' target='_blank' style='display:inline-block;padding:14px 28px;font-family:Helvetica Neue,Helvetica,Arial,-apple-system,BlinkMacSystemFont,Segoe UI,Roboto,sans-serif;font-size:15px;font-weight:600;line-height:20px;letter-spacing:0.2px;color:#FFFFFF;background-color:#2563EB;border-radius:6px;mso-padding-alt:0;mso-hide:all;'>{actionText}</a>
                                    <!--<![endif]-->
                                </td>
                            </tr>
                        </table>
                      </td>
                    </tr>";
            }
            else
            {
                actionButtonHtml = @"
                    <tr>
                      <td class='card px' style='background-color:#FFFFFF; padding:0 40px 8px 40px; border-radius:0 0 8px 8px; font-size:0; line-height:0;'>&nbsp;</td>
                    </tr>";
            }

            return templateHtml
                .Replace("{{TITLE}}", title)
                .Replace("{{CONTENT}}", content)
                .Replace("{{ACTION_BUTTON}}", actionButtonHtml)
                .Replace("{{YEAR}}", year);
        }
    }
}
