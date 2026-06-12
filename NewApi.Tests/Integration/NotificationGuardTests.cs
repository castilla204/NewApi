using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewApi.Tests.Builders;
using NewApi.Tests.Fixtures;
using newApi.Services;

namespace NewApi.Tests.Integration;

/// <summary>
/// 🛡️ NOTIF-GUARD (2026-06-12): los logs CRITICAL son alertas de SISTEMA (balance
/// de Stripe, PaymentIntentIds, "ACTION REQUIRED"...) y solo deben llegar a los
/// administradores (fila global UserId=null + email). Caso real: el inbox del
/// CLIENTE mostraba "CRITICAL: Insufficient Stripe platform balance..." con el
/// plan de distribución y el PaymentIntentId.
///
/// Contrato:
///   - LogCriticalAsync(notifyUser: true) SIN userNotificationMessage → NO se crea
///     notificación para el usuario (los internals quedan en log + alerta admin).
///   - Con userNotificationMessage → se crea UNA notificación con SOLO ese texto
///     (sin message/details internos) y título de aviso, no "Alerta Crítica".
///   - LogInfoAsync(notifyUser: true) → sin cambios (mensajes redactados para el
///     usuario, p.ej. "Cita confirmada por el experto").
/// </summary>
[Collection("Api")]
public class NotificationGuardTests
{
    private readonly ApiFactoryFixture _api;

    public NotificationGuardTests(ApiFactoryFixture api) => _api = api;

    private ILoggingService ResolveLogging(out IServiceScope scope)
    {
        scope = _api.Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ILoggingService>();
    }

    [Fact(DisplayName = "NG-01 · Critical con notifyUser SIN mensaje saneado → el usuario NO recibe nada")]
    public async Task Critical_without_user_message_is_not_delivered_to_user()
    {
        int userId;
        await using (var db = _api.CreateDbContext())
        {
            var user = await new UserBuilder("ng01@test.dev").AsClient().Verified().PersistAsync(db);
            userId = user.Id;
        }

        var logging = ResolveLogging(out var scope);
        using (scope)
        {
            await logging.LogCriticalAsync(
                message: "CRITICAL NG-01: Insufficient Stripe platform balance for money distribution",
                details: "Available Balance: 28,77 EUR, PaymentIntentId: pi_test_ng01. ACTION REQUIRED: retry distribution.",
                userId: userId,
                source: "NotificationGuardTests",
                notifyUser: true);
        }

        await using var verify = _api.CreateDbContext();
        var userNotifications = await verify.Notifications
            .Where(n => n.UserId == userId)
            .ToListAsync();
        userNotifications.Should().BeEmpty(
            "un log Critical sin userNotificationMessage es una alerta de sistema: solo admins (fila global + email)");
    }

    [Fact(DisplayName = "NG-02 · Critical CON mensaje saneado → el usuario recibe solo ese texto, sin internals")]
    public async Task Critical_with_user_message_delivers_only_sanitized_text()
    {
        int userId;
        await using (var db = _api.CreateDbContext())
        {
            var user = await new UserBuilder("ng02@test.dev").AsClient().Verified().PersistAsync(db);
            userId = user.Id;
        }

        const string sanitized = "El movimiento de dinero de tu servicio está tardando un poco más de lo habitual. No tienes que hacer nada.";

        var logging = ResolveLogging(out var scope);
        using (scope)
        {
            await logging.LogCriticalAsync(
                message: "CRITICAL NG-02: Insufficient Stripe platform balance for money distribution",
                details: "Available Balance: 1,00 EUR, PaymentIntentId: pi_test_ng02. ACTION REQUIRED.",
                userId: userId,
                source: "NotificationGuardTests",
                notifyUser: true,
                userNotificationMessage: sanitized);
        }

        await using var verify = _api.CreateDbContext();
        var notif = await verify.Notifications.SingleOrDefaultAsync(n => n.UserId == userId);
        notif.Should().NotBeNull("con mensaje saneado el usuario SÍ debe ser avisado");
        notif!.Message.Should().Be(sanitized, "el texto del usuario no debe contener message/details internos");
        notif.Message.Should().NotContain("PaymentIntent").And.NotContain("ACTION REQUIRED").And.NotContain("CRITICAL");
        notif.Title.Should().NotContain("Alerta Crítica", "para el usuario es un aviso, no una alerta interna");
    }

    [Fact(DisplayName = "NG-03 · Info con notifyUser (flujo normal) sigue llegando al usuario")]
    public async Task Info_notifications_still_reach_the_user()
    {
        int userId;
        await using (var db = _api.CreateDbContext())
        {
            var user = await new UserBuilder("ng03@test.dev").AsClient().Verified().PersistAsync(db);
            userId = user.Id;
        }

        var logging = ResolveLogging(out var scope);
        using (scope)
        {
            await logging.LogInfoAsync(
                message: "Cita confirmada por el experto NG-03",
                details: "El experto confirmó la cita para el 16/06/2026 11:00",
                userId: userId,
                source: "NotificationGuardTests",
                notifyUser: true);
        }

        await using var verify = _api.CreateDbContext();
        var notif = await verify.Notifications.SingleOrDefaultAsync(n => n.UserId == userId);
        notif.Should().NotBeNull("las notificaciones Info redactadas para el usuario no cambian");
        notif!.Message.Should().Contain("Cita confirmada por el experto NG-03");
    }
}
