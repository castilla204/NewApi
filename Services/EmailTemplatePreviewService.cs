using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Services
{
    public class EmailTemplatePreviewDto
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public string Group { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Html { get; set; } = "";
    }

    /// <summary>
    /// Catálogo de plantillas de email renderizadas con datos de ejemplo, para el preview admin.
    /// Reutiliza los mismos métodos Render… que el envío real → cero drift.
    /// </summary>
    public static class EmailTemplatePreviewService
    {
        public static List<EmailTemplatePreviewDto> GetAll()
        {
            const string userName = "Juan Pérez";
            const string date = "lunes 30 de junio, 10:00";
            const string location = "Calle Mayor 1, Madrid";
            const string serviceName = "Inspección pre-compra";
            const string expertName = "Carlos Ruiz";
            const int hireId = 1234;

            var list = new List<EmailTemplatePreviewDto>();

            void Add(string key, string label, string group, (string subject, string html) r)
                => list.Add(new EmailTemplatePreviewDto { Key = key, Label = label, Group = group, Subject = r.subject, Html = r.html });

            Add("welcome", "Bienvenida", "Usuario", NotificationService.RenderWelcome(userName));
            Add("appointment-client", "Confirmación de cita (cliente)", "Transaccional", NotificationService.RenderAppointmentConfirmation(userName, date, location, false, hireId));
            Add("appointment-expert", "Confirmación de cita (experto)", "Transaccional", NotificationService.RenderAppointmentConfirmation(expertName, date, location, true, hireId));
            Add("service-completion", "Servicio completado", "Transaccional", NotificationService.RenderServiceCompletion(userName, serviceName, expertName, hireId));
            Add("general-notification", "Notificación general", "Usuario", NotificationService.RenderGeneralNotification(userName, "Actualización importante", "Hemos actualizado los términos de tu contratación.", "Ver detalles", $"https://inspecciono.com/hires/{hireId}"));
            // Email REAL de la campana (LoggingService) — mismo render que el envío → cero drift.
            Add("user-notification", "Notificación (campana)", "Usuario", NotificationService.RenderUserNotification("Información", "Cuenta de Stripe aprobada - Tu cuenta está activa y lista para cobrar.", "Ver notificaciones", "https://inspecciono.com/notifications"));
            // Emails al VENDEDOR (tercero sin cuenta, modo Coordínalo).
            Add("seller-magic-link", "Magic-link vendedor (coordinar cita)", "Transaccional", NotificationService.RenderSellerMagicLink("https://inspecciono.com/appointment/schedule/AbC123ejemplo"));
            Add("seller-magic-link-reminder", "Recordatorio magic-link vendedor", "Transaccional", NotificationService.RenderSellerMagicLink("https://inspecciono.com/appointment/schedule/AbC123ejemplo", reminder: true));
            Add("seller-booking-confirmed", "Acuse reserva del vendedor", "Transaccional", NotificationService.RenderSellerBookingConfirmed(date, location));
            Add("seller-outcome", "Desenlace al vendedor (sin cuenta)", "Transaccional", NotificationService.RenderGeneralNotification("", "Inspección de tu vehículo confirmada", $"El técnico ha confirmado la inspección de tu vehículo para el {date}. Se coordinará contigo.", null, null));
            Add("otp-email-verification", "OTP · Verificar correo", "OTP", NotificationService.RenderVerificationCode("482913", EmailVerificationPurpose.EmailVerification, 10));
            Add("otp-password-reset", "OTP · Restablecer contraseña", "OTP", NotificationService.RenderVerificationCode("482913", EmailVerificationPurpose.PasswordReset, 10));
            Add("otp-stepup", "OTP · Confirmación de seguridad", "OTP", NotificationService.RenderVerificationCode("482913", EmailVerificationPurpose.StepUp, 10));
            Add("invoice", "Email de factura", "Transaccional", InvoiceService.RenderInvoiceEmail(userName, serviceName, expertName, 120.50m, "EUR", null, hireId));

            Add("admin-digest", "Digest de alertas admin", "Interno", LoggingService.RenderAdminDigest(new List<LoggingService.AdminDigestRow>
            {
                new("StripeWebhook", "transfer.failed sin reintento", "SearchHire", "1234", 7, DateTime.UtcNow.AddMinutes(-40), DateTime.UtcNow.AddMinutes(-2)),
                new("RefundService", "currency mismatch", "SearchHire", "1240", 4, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-5)),
            }));

            Add("refund-failed-digest", "Digest de refunds fallidos", "Interno", LoggingService.RenderRefundFailedDigest(new List<LoggingService.RefundDigestRow>
            {
                new(1234, "RefundPending", true, 11, 22, 120.50m, DateTime.UtcNow.AddHours(-3)),
                new(1240, "RequiresManualReview", false, 13, 24, 89.00m, DateTime.UtcNow.AddHours(-20)),
            }));

            return list;
        }
    }
}
