using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using newApi.Content;

namespace newApi.Services;

public class SupportChatService : ISupportChatService
{
    private const string Model = "gpt-4o-mini";
    private const int MaxUserMessageLength = 1200;
    private const int MaxHistoryTurns = 8;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SupportChatService> _logger;

    public SupportChatService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<SupportChatService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> AskAsync(
        string userMessage,
        IReadOnlyList<SupportChatTurn>? history,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("OpenAI API key not configured for support chat");
            throw new InvalidOperationException("El asistente no está disponible en este momento.");
        }

        var trimmed = userMessage.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException("El mensaje no puede estar vacío.");

        if (trimmed.Length > MaxUserMessageLength)
            throw new ArgumentException($"El mensaje no puede superar {MaxUserMessageLength} caracteres.");

        var systemContent = $"{SupportChatKnowledge.SystemInstructions}\n\n--- CONOCIMIENTO ---\n{SupportChatKnowledge.KnowledgeBase}";

        var messages = new List<object>
        {
            new { role = "system", content = systemContent },
        };

        if (history != null)
        {
            foreach (var turn in history.TakeLast(MaxHistoryTurns))
            {
                var role = turn.Role?.ToLowerInvariant();
                if (role is not ("user" or "assistant")) continue;
                if (string.IsNullOrWhiteSpace(turn.Content)) continue;
                messages.Add(new { role, content = turn.Content.Trim() });
            }
        }

        messages.Add(new { role = "user", content = trimmed });

        var requestBody = new
        {
            model = Model,
            messages,
            max_tokens = 800,
            temperature = 0.25,
        };

        var client = _httpClientFactory.CreateClient("openai");
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("OpenAI support chat failed: {Status} {Body}", response.StatusCode, responseText);
            throw new InvalidOperationException("No he podido generar una respuesta. Inténtalo de nuevo en unos segundos.");
        }

        var completion = JsonSerializer.Deserialize<ChatCompletionResponse>(responseText);
        var reply = completion?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();

        if (string.IsNullOrEmpty(reply))
        {
            _logger.LogError("OpenAI support chat empty reply: {Body}", responseText);
            throw new InvalidOperationException("No he podido generar una respuesta. Inténtalo de nuevo.");
        }

        return reply;
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public ChatChoice[]? Choices { get; set; }
    }

    private sealed class ChatChoice
    {
        [JsonPropertyName("message")]
        public ChatMessage? Message { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
