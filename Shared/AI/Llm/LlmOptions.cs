namespace LocalList.API.NET.Shared.AI.Llm;

/// <summary>
/// Cadena ordenada de proveedores LLM para el camino crítico (chat + builder).
/// Bind desde config "Llm". Un provider sin API key configurada se omite de la cadena en boot.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public List<LlmProviderEntry> Providers { get; set; } = new();
}

public sealed class LlmProviderEntry
{
    /// <summary>"gemini" | "openai" | "mistral" | "anthropic". Determina la clase provider y el named HttpClient ("llm-{name}").</summary>
    public string Name { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>Solo providers OpenAI-compatible (p. ej. https://api.openai.com/v1, https://api.mistral.ai/v1).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Clave de IConfiguration donde vive la API key (p. ej. "Gemini:ApiKey", "OpenAI:ApiKey").</summary>
    public string ApiKeyConfigKey { get; set; } = string.Empty;
}
