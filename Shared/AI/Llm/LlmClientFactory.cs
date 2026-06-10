using LocalList.API.NET.Shared.AI.Llm.Providers;
using Microsoft.Extensions.Options;

namespace LocalList.API.NET.Shared.AI.Llm;

/// <summary>
/// Construye la cadena de fallback desde config Llm:Providers.
/// Un provider sin API key se omite (log en boot vía LogEffectiveChain).
/// Cada provider usa el named HttpClient "llm-{name}" con su resilience handler.
/// </summary>
public static class LlmClientFactory
{
    public static ILlmClient BuildChain(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
        var config = sp.GetRequiredService<IConfiguration>();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var health = sp.GetRequiredService<LlmProviderHealthRegistry>();

        var chain = new List<ILlmClient>();
        foreach (var entry in options.Providers)
        {
            var apiKey = config[entry.ApiKeyConfigKey];
            if (string.IsNullOrEmpty(apiKey)) continue;

            var http = httpFactory.CreateClient($"llm-{entry.Name}");
            var logger = loggerFactory.CreateLogger($"LocalList.Llm.{entry.Name}");

            ILlmClient? client = entry.Name.ToLowerInvariant() switch
            {
                "gemini" => new GeminiLlmClient(http, apiKey, entry.Model, logger),
                "openai" => new OpenAiCompatibleLlmClient(
                    http, apiKey, "openai", entry.BaseUrl ?? "https://api.openai.com/v1", entry.Model, logger,
                    usesMaxCompletionTokens: true, supportsTemperature: false,
                    minOutputTokens: 1024, reasoningEffort: "minimal"),
                "mistral" => new OpenAiCompatibleLlmClient(
                    http, apiKey, "mistral", entry.BaseUrl ?? "https://api.mistral.ai/v1", entry.Model, logger),
                "anthropic" => new AnthropicLlmClient(http, apiKey, entry.Model, logger),
                _ => null,
            };

            if (client != null) chain.Add(client);
        }

        return new FallbackLlmClient(chain, health, loggerFactory.CreateLogger<FallbackLlmClient>());
    }

    /// <summary>Loggea en boot qué providers quedaron en la cadena efectiva y cuáles se omitieron.</summary>
    public static void LogEffectiveChain(IConfiguration config, LlmOptions options, ILogger logger)
    {
        var active = new List<string>();
        var skipped = new List<string>();
        foreach (var entry in options.Providers)
        {
            if (string.IsNullOrEmpty(config[entry.ApiKeyConfigKey]))
                skipped.Add($"{entry.Name}({entry.ApiKeyConfigKey} missing)");
            else
                active.Add($"{entry.Name}:{entry.Model}");
        }
        logger.LogInformation("LLM fallback chain: [{Active}]{Skipped}",
            string.Join(" → ", active),
            skipped.Count > 0 ? $" — skipped: {string.Join(", ", skipped)}" : string.Empty);
    }
}
