using Microsoft.Extensions.Configuration;
using NewApi.Tests.Fixtures;
using newApi.Services;
using newApi.DataLayer.Models.PostGresModels;

namespace NewApi.Tests.Integration;

/// <summary>
/// 📱 SMS-CENTRAL: el SmsService está GATED por configuración. Sin credenciales o sin
/// emisor (FromNumber/MessagingServiceSid), IsEnabled=false y SendSmsAsync es un no-op
/// que devuelve false SIN lanzar — así el código vive desplegado sin riesgo y se activa
/// solo al poner los secrets. Estos tests fijan ese contrato (sin tocar Twilio real).
/// </summary>
public class SmsNotificationTests
{
    private static SmsService Build(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        // Logging real no hace falta; usamos un stub mínimo.
        return new SmsService(config, new NoopLogging());
    }

    [Fact(DisplayName = "SMS-01 · sin credenciales → IsEnabled=false y SendSmsAsync devuelve false (no-op)")]
    public async Task Disabled_without_credentials()
    {
        var sms = Build();
        sms.IsEnabled.Should().BeFalse();
        (await sms.SendSmsAsync("+34600000000", "hola")).Should().BeFalse("sin credenciales es un no-op seguro");
    }

    [Fact(DisplayName = "SMS-02 · con credenciales pero SIN emisor → sigue deshabilitado")]
    public async Task Disabled_without_sender()
    {
        var sms = Build(("Twilio:AccountSid", "ACtest"), ("Twilio:AuthToken", "tok"));
        sms.IsEnabled.Should().BeFalse("falta FromNumber o MessagingServiceSid");
        (await sms.SendSmsAsync("+34600000000", "hola")).Should().BeFalse();
    }

    [Fact(DisplayName = "SMS-03 · credenciales + emisor → IsEnabled=true (se activa solo al configurar)")]
    public void Enabled_with_credentials_and_sender()
    {
        var sms = Build(("Twilio:AccountSid", "ACtest"), ("Twilio:AuthToken", "tok"), ("Twilio:FromNumber", "+34900000000"));
        sms.IsEnabled.Should().BeTrue();
    }

    [Fact(DisplayName = "SMS-04 · número/mensaje vacío → false sin intentar enviar")]
    public async Task Empty_inputs_short_circuit()
    {
        var sms = Build(("Twilio:AccountSid", "ACtest"), ("Twilio:AuthToken", "tok"), ("Twilio:FromNumber", "+34900000000"));
        (await sms.SendSmsAsync("", "hola")).Should().BeFalse();
        (await sms.SendSmsAsync("+34600000000", "")).Should().BeFalse();
    }

    private sealed class NoopLogging : ILoggingService
    {
        public Task LogCriticalAsync(string m, string? d = null, int? u = null, string? s = null, string? rt = null, int? ri = null, object? a = null, bool n = false, string? un = null) => Task.CompletedTask;
        public Task LogErrorAsync(string m, string? d = null, int? u = null, string? s = null, string? rt = null, int? ri = null, object? a = null, bool n = false, string? un = null) => Task.CompletedTask;
        public Task LogWarningAsync(string m, string? d = null, int? u = null, string? s = null, string? rt = null, int? ri = null, object? a = null, bool n = false, string? un = null) => Task.CompletedTask;
        public Task LogInfoAsync(string m, string? d = null, int? u = null, string? s = null, string? rt = null, int? ri = null, object? a = null, bool n = false, string? un = null) => Task.CompletedTask;
        public Task LogDebugAsync(string m, string? d = null, int? u = null, string? s = null, string? rt = null, int? ri = null, object? a = null, bool n = false, string? un = null) => Task.CompletedTask;
        public Task<LogType?> GetLogTypeAsync(string name) => Task.FromResult<LogType?>(null);
        public Task<LogType> CreateLogTypeAsync(string name, string? description = null, string? severityName = null, bool requiresAdminNotification = false, bool requiresEmailAlert = false, bool requiresSmsAlert = false) => Task.FromResult(new LogType());
        public Task EmitAdminDigestAsync() => Task.CompletedTask;
        public Task EmitRefundFailedDigestAsync() => Task.CompletedTask;
    }
}
