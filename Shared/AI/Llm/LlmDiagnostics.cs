namespace LocalList.API.NET.Shared.AI.Llm;

/// <summary>Truncados compartidos por todos los providers (límites de chat_turns).</summary>
internal static class LlmDiagnostics
{
    internal static string TruncatePrompt(string prompt) =>
        prompt.Length <= 4096 ? prompt : prompt[..4096];

    internal static string TruncateResponse(string response) =>
        response.Length <= 8192 ? response : response[..8192];
}
