namespace ClaudetRelay.Services;

public sealed class OpenRouterService : OpenAICompatibleService
{
    public override string ProviderName => "OpenRouter";

    public static readonly string[] DefaultModels =
    [
        "anthropic/claude-sonnet-4",
        "google/gemini-2.0-flash",
        "openai/gpt-4o",
        "meta-llama/llama-3.3-70b-instruct",
        "mistralai/mistral-small",
    ];

    public OpenRouterService(string apiKey)
        : base("https://openrouter.ai/api/v1", apiKey,
               httpReferer: "https://github.com/ClaudetRelay",
               appTitle:    "ClaudetRelay")
    {
        CurrentModel = DefaultModels[0];
    }
}
