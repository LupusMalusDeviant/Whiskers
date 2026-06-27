using ServerWatch.Configuration;

namespace ServerWatch.Services.Agent.Providers;

/// <summary>Selects the matching provider impl based on the AgentSettings and configures base URL +
/// key. openai/openrouter/ollama share the OpenAI-compatible client; gemini/anthropic come
/// as their own impls.</summary>
public sealed class AgentProviderFactory : IAgentProviderFactory
{
    public const string OpenAiEndpoint = "https://api.openai.com/v1/chat/completions";
    public const string OpenRouterEndpoint = "https://openrouter.ai/api/v1/chat/completions";
    public const string OllamaEndpoint = "http://localhost:11434/v1/chat/completions";
    public const string GeminiEndpoint = "https://generativelanguage.googleapis.com/v1beta/models";
    public const string AnthropicEndpoint = "https://api.anthropic.com/v1/messages";

    private readonly IHttpClientFactory _httpFactory;

    public AgentProviderFactory(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

    /// <summary>Empty/whitespace endpoint from configuration → null (provider default applies).</summary>
    public static string? NormalizeEndpoint(string? endpoint) =>
        string.IsNullOrWhiteSpace(endpoint) ? null : endpoint;

    public IReadOnlyCollection<string> SupportedProviderIds { get; } =
        new[] { "openai", "openrouter", "ollama", "gemini", "anthropic" };

    public IAgentLlmProvider Resolve(AgentSettings settings)
    {
        var http = _httpFactory.CreateClient("agent");
        var id = settings.Provider.ToLowerInvariant();
        // An empty config string is NOT a valid endpoint → treat it as unset.
        var endpoint = NormalizeEndpoint(settings.Endpoint);
        return id switch
        {
            "openai" => new OpenAiCompatibleProvider(http, id, settings.ApiKey, endpoint ?? OpenAiEndpoint),
            "openrouter" => new OpenAiCompatibleProvider(http, id, settings.ApiKey, endpoint ?? OpenRouterEndpoint),
            "ollama" => new OpenAiCompatibleProvider(http, id, settings.ApiKey, endpoint ?? OllamaEndpoint),
            "gemini" => new GeminiProvider(http, settings.ApiKey, endpoint ?? GeminiEndpoint),
            "anthropic" => new AnthropicProvider(http, settings.ApiKey, endpoint ?? AnthropicEndpoint),
            _ => throw new NotSupportedException($"Unbekannter Provider '{settings.Provider}'."),
        };
    }
}
