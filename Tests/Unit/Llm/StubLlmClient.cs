using LocalList.API.NET.Shared.AI.Llm;
using LocalList.API.NET.Shared.Observability;

namespace LocalList.API.Tests.Unit.Llm;

/// <summary>
/// Stub de ILlmClient para unit tests: respuesta programable por llamada,
/// sin HTTP. Cuenta las invocaciones para asserts de la cadena de fallback.
/// </summary>
public sealed class StubLlmClient : ILlmClient
{
    private readonly Func<LlmJsonRequest, LlmJsonResponse> _responder;

    public string ProviderName { get; }
    public string Model { get; }
    public int CallCount { get; private set; }

    public StubLlmClient(string providerName, Func<LlmJsonRequest, LlmJsonResponse> responder, string model = "stub-model")
    {
        ProviderName = providerName;
        Model = model;
        _responder = responder;
    }

    public Task<LlmJsonResponse> GenerateJsonAsync(LlmJsonRequest request, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(_responder(request));
    }

    public static StubLlmClient Succeeding(string providerName, string json = "{}") =>
        new(providerName, req => new LlmJsonResponse(json, OkDiagnostics(providerName, req)));

    public static StubLlmClient Failing(string providerName, string errorCode = "http_error", int? httpStatus = 503) =>
        new(providerName, req => new LlmJsonResponse(null, new AiCallDiagnostics(
            Provider: providerName, Model: "stub-model",
            Prompt: req.Prompt, ResponseRaw: null, FinishReason: null, LatencyMs: 5,
            InputTokens: null, OutputTokens: null, ThinkingTokens: null, TotalTokens: null,
            CostUsd: null, HttpStatus: httpStatus, ErrorCode: errorCode, ErrorMessage: errorCode)));

    public static AiCallDiagnostics OkDiagnostics(string providerName, LlmJsonRequest req) =>
        new(Provider: providerName, Model: "stub-model",
            Prompt: req.Prompt, ResponseRaw: "{}", FinishReason: "STOP", LatencyMs: 5,
            InputTokens: 100, OutputTokens: 50, ThinkingTokens: null, TotalTokens: 150,
            CostUsd: null, HttpStatus: 200, ErrorCode: null, ErrorMessage: null);
}
