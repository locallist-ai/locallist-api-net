namespace LocalList.API.NET.Shared.AI.Llm;

/// <summary>Truncados compartidos por todos los providers (límites de chat_turns).</summary>
internal static class LlmDiagnostics
{
    internal static string TruncatePrompt(string prompt) =>
        prompt.Length <= 4096 ? prompt : prompt[..4096];

    internal static string TruncateResponse(string response) =>
        response.Length <= 8192 ? response : response[..8192];

    /// <summary>
    /// Recorte agresivo (~500 chars) del cuerpo de un error no-2xx para incrustarlo en
    /// ErrorMessage (diagnóstico admin). Mantiene error.message/quotaId sin volcar payloads
    /// enormes en chat_turns. El redactado de PII corre aparte (PiiRedactor).
    /// </summary>
    internal const int ErrorBodyMax = 500;

    internal static string TruncateErrorBody(string body) =>
        body.Length <= ErrorBodyMax ? body : body[..ErrorBodyMax];
}
