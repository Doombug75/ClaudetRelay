namespace ClaudetRelay.Services;

/// <summary>Unified message type for all cloud AI providers.</summary>
public record CloudAIMessage(string Role, string Content, string Sender = "");

/// <summary>Contract every cloud AI provider service must implement.</summary>
public interface ICloudAIService : IDisposable
{
    /// <summary>Human-readable provider name, e.g. "Anthropic", "Groq".</summary>
    string ProviderName { get; }

    string CurrentModel { get; set; }

    Task<string> SendAsync(
        IReadOnlyList<CloudAIMessage> messages,
        string? system = null,
        CancellationToken ct = default);

    IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<CloudAIMessage> messages,
        string? system = null,
        CancellationToken ct = default);

    Task<bool>         IsAvailableAsync(CancellationToken ct = default);
    Task<List<string>> GetModelsAsync  (CancellationToken ct = default);
}
