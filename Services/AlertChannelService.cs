using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace newApi.Services
{
    public interface IAlertChannelService
    {
        Task<AlertChannelResult> SendCriticalAsync(string message, object? additionalData = null, CancellationToken cancellationToken = default);
        AlertChannelStatus GetStatus();
    }

    public sealed class AlertChannelResult
    {
        public bool SlackAttempted { get; set; }
        public bool SlackSucceeded { get; set; }
        public bool TelegramAttempted { get; set; }
        public bool TelegramSucceeded { get; set; }
        public string? Error { get; set; }
        public bool AnyDelivered => SlackSucceeded || TelegramSucceeded;
    }

    public sealed class AlertChannelStatus
    {
        public bool SlackConfigured { get; set; }
        public bool TelegramConfigured { get; set; }
    }

    /// <summary>
    /// P3-3: Canal alternativo de alertas (Slack/Telegram webhook). Pensado como fallback
    /// cuando SMTP falla o no está configurado. Nunca debe lanzar excepción al caller.
    /// </summary>
    public class AlertChannelService : IAlertChannelService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

        public AlertChannelService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        public AlertChannelStatus GetStatus()
        {
            var slackUrl = _configuration["AlertChannels:Slack:WebhookUrl"];
            var telegramToken = _configuration["AlertChannels:Telegram:BotToken"];
            var telegramChatId = _configuration["AlertChannels:Telegram:ChatId"];
            return new AlertChannelStatus
            {
                SlackConfigured = !string.IsNullOrWhiteSpace(slackUrl),
                TelegramConfigured = !string.IsNullOrWhiteSpace(telegramToken) && !string.IsNullOrWhiteSpace(telegramChatId)
            };
        }

        public async Task<AlertChannelResult> SendCriticalAsync(string message, object? additionalData = null, CancellationToken cancellationToken = default)
        {
            var result = new AlertChannelResult();
            if (string.IsNullOrWhiteSpace(message))
            {
                result.Error = "Empty message";
                return result;
            }

            var enrichedText = additionalData != null
                ? $"{message}\n\n```{SafeSerialize(additionalData)}```"
                : message;

            var slackUrl = _configuration["AlertChannels:Slack:WebhookUrl"];
            if (!string.IsNullOrWhiteSpace(slackUrl))
            {
                result.SlackAttempted = true;
                result.SlackSucceeded = await TrySendSlackAsync(slackUrl!, enrichedText, cancellationToken);
                if (result.SlackSucceeded)
                {
                    return result;
                }
            }

            var telegramToken = _configuration["AlertChannels:Telegram:BotToken"];
            var telegramChatId = _configuration["AlertChannels:Telegram:ChatId"];
            if (!string.IsNullOrWhiteSpace(telegramToken) && !string.IsNullOrWhiteSpace(telegramChatId))
            {
                result.TelegramAttempted = true;
                result.TelegramSucceeded = await TrySendTelegramAsync(telegramToken!, telegramChatId!, enrichedText, cancellationToken);
                if (result.TelegramSucceeded)
                {
                    return result;
                }
            }

            if (!result.AnyDelivered)
            {
                Console.Error.WriteLine($"[ALERT CHANNEL] Ningún canal entregó la alerta. Slack(intentado={result.SlackAttempted},ok={result.SlackSucceeded}) Telegram(intentado={result.TelegramAttempted},ok={result.TelegramSucceeded}). Mensaje: {message}");
            }

            return result;
        }

        private async Task<bool> TrySendSlackAsync(string webhookUrl, string text, CancellationToken cancellationToken)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(DefaultTimeout);

                var client = _httpClientFactory.CreateClient("AlertChannel");
                client.Timeout = DefaultTimeout;
                var payload = JsonSerializer.Serialize(new { text });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(webhookUrl, content, cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ALERT CHANNEL] Slack fallo: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> TrySendTelegramAsync(string botToken, string chatId, string text, CancellationToken cancellationToken)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(DefaultTimeout);

                var client = _httpClientFactory.CreateClient("AlertChannel");
                client.Timeout = DefaultTimeout;
                var url = $"https://api.telegram.org/bot{botToken}/sendMessage";
                var payload = JsonSerializer.Serialize(new { chat_id = chatId, text });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(url, content, cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ALERT CHANNEL] Telegram fallo: {ex.Message}");
                return false;
            }
        }

        private static string SafeSerialize(object data)
        {
            try
            {
                return JsonSerializer.Serialize(data);
            }
            catch
            {
                return data?.ToString() ?? string.Empty;
            }
        }
    }
}
