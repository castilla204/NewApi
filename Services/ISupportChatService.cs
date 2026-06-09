namespace newApi.Services;

public interface ISupportChatService
{
    Task<string> AskAsync(string userMessage, IReadOnlyList<SupportChatTurn>? history, CancellationToken cancellationToken = default);
}

public sealed class SupportChatTurn
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
