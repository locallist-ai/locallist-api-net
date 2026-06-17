using LocalList.API.NET.Shared.AI.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LocalList.API.Tests.Unit.Llm;

public class FallbackLlmClientTests
{
    private static readonly LlmJsonRequest Request = new("test prompt", 0.2, 200);

    private static FallbackLlmClient Chain(params ILlmClient[] providers) =>
        new(providers, new LlmProviderHealthRegistry(new FakeTimeProvider()),
            NullLogger<FallbackLlmClient>.Instance);

    [Fact]
    public async Task PrimarySucceeds_ReturnsPrimaryWithoutCallingSecondary()
    {
        var primary = StubLlmClient.Succeeding("gemini", "{\"ok\":true}");
        var secondary = StubLlmClient.Succeeding("openai");

        var response = await Chain(primary, secondary).GenerateJsonAsync(Request);

        Assert.True(response.Succeeded);
        Assert.Equal("gemini", response.Diagnostics.Provider);
        Assert.Equal(1, response.Diagnostics.Attempt);
        Assert.Equal(0, secondary.CallCount);
    }

    [Fact]
    public async Task PrimaryFails_FallsBackToSecondary()
    {
        var primary = StubLlmClient.Failing("gemini", "http_error", 503);
        var secondary = StubLlmClient.Succeeding("openai", "{\"ok\":true}");

        var response = await Chain(primary, secondary).GenerateJsonAsync(Request);

        Assert.True(response.Succeeded);
        Assert.Equal("openai", response.Diagnostics.Provider);
        Assert.Equal(2, response.Diagnostics.Attempt);
        Assert.Equal(1, primary.CallCount);
    }

    [Fact]
    public async Task PrimaryReturnsNonJson_TreatedAsFailure_FallsBack()
    {
        var primary = StubLlmClient.Succeeding("gemini", "I am sorry, I cannot produce JSON");
        var secondary = StubLlmClient.Succeeding("openai", "{\"ok\":true}");

        var response = await Chain(primary, secondary).GenerateJsonAsync(Request);

        Assert.True(response.Succeeded);
        Assert.Equal("openai", response.Diagnostics.Provider);
    }

    [Theory]
    [InlineData("```json\n{\"ok\":true}\n```")]
    [InlineData("```json\r\n{\"ok\":true}\r\n```")]   // CRLF tras la apertura
    [InlineData("```JSON\n{\"ok\":true}\n```")]       // lenguaje en mayúsculas
    [InlineData("```\n{\"ok\":true}\n```")]           // sin etiqueta de lenguaje
    [InlineData("  ```json\n{\"ok\":true}\n```  ")]   // espacios alrededor
    public async Task MarkdownFences_AreCleanedFromSuccessfulText(string raw)
    {
        var primary = StubLlmClient.Succeeding("gemini", raw);

        var response = await Chain(primary).GenerateJsonAsync(Request);

        Assert.True(response.Succeeded);
        Assert.Equal("{\"ok\":true}", response.Text);
    }

    [Fact]
    public async Task ProviderThrows_CaughtAndFallsBackToNext()
    {
        // El caso para el que existe la cadena: un provider se cuelga (lanza) en vez de devolver
        // un fallo estructurado. Antes la excepción abortaba toda la cadena → 500. Ahora cae al siguiente.
        var primary = StubLlmClient.Throwing("gemini", new InvalidOperationException("hung"));
        var secondary = StubLlmClient.Succeeding("openai", "{\"ok\":true}");

        var response = await Chain(primary, secondary).GenerateJsonAsync(Request);

        Assert.True(response.Succeeded);
        Assert.Equal("openai", response.Diagnostics.Provider);
        Assert.Equal(1, primary.CallCount);
    }

    [Fact]
    public async Task AllProvidersThrow_ReturnsProviderErrorWithSummary()
    {
        var primary = StubLlmClient.Throwing("gemini", new InvalidOperationException("hung"));
        var secondary = StubLlmClient.Throwing("openai", new TimeoutException("polly timeout"));

        var response = await Chain(primary, secondary).GenerateJsonAsync(Request);

        Assert.False(response.Succeeded);
        Assert.Equal("provider_error", response.Diagnostics.ErrorCode);
        Assert.Contains("gemini: provider_error", response.Diagnostics.ErrorMessage);
        Assert.Contains("openai: provider_error", response.Diagnostics.ErrorMessage);
    }

    [Fact]
    public async Task ProviderThrowsOnCallerCancellation_Propagates()
    {
        // Si el provider lanza OCE porque el caller canceló a mitad de llamada, la cadena NO debe
        // tragárselo como fallback — el turno se está cancelando de verdad. (ct no está cancelado al
        // entrar al bucle, así que esto ejercita el guard del catch, no el ThrowIfCancellationRequested.)
        using var cts = new CancellationTokenSource();
        var primary = new StubLlmClient("gemini", _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var secondary = StubLlmClient.Succeeding("openai", "{\"ok\":true}");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Chain(primary, secondary).GenerateJsonAsync(Request, cts.Token));

        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, secondary.CallCount);
    }

    [Fact]
    public async Task AllProvidersFail_ReturnsLastDiagnosticsWithAttemptSummary()
    {
        var primary = StubLlmClient.Failing("gemini", "http_error", 503);
        var secondary = StubLlmClient.Failing("openai", "timeout", null);

        var response = await Chain(primary, secondary).GenerateJsonAsync(Request);

        Assert.False(response.Succeeded);
        Assert.Equal("openai", response.Diagnostics.Provider);
        Assert.Equal("timeout", response.Diagnostics.ErrorCode);
        Assert.Contains("gemini: http_error(503)", response.Diagnostics.ErrorMessage);
        Assert.Contains("openai: timeout", response.Diagnostics.ErrorMessage);
    }

    [Fact]
    public async Task OpenCircuit_SkipsProviderWithoutCallingIt()
    {
        var time = new FakeTimeProvider();
        var health = new LlmProviderHealthRegistry(time);
        for (var i = 0; i < 3; i++) health.RecordFailure("gemini");

        var primary = StubLlmClient.Succeeding("gemini");
        var secondary = StubLlmClient.Succeeding("openai", "{\"ok\":true}");
        var chain = new FallbackLlmClient(
            new ILlmClient[] { primary, secondary }, health, NullLogger<FallbackLlmClient>.Instance);

        var response = await chain.GenerateJsonAsync(Request);

        Assert.True(response.Succeeded);
        Assert.Equal("openai", response.Diagnostics.Provider);
        Assert.Equal(0, primary.CallCount);
    }

    [Fact]
    public async Task RepeatedFailures_OpenCircuitAfterThreshold()
    {
        var time = new FakeTimeProvider();
        var health = new LlmProviderHealthRegistry(time);
        var primary = StubLlmClient.Failing("gemini");
        var secondary = StubLlmClient.Succeeding("openai", "{}");
        var chain = new FallbackLlmClient(
            new ILlmClient[] { primary, secondary }, health, NullLogger<FallbackLlmClient>.Instance);

        for (var i = 0; i < 3; i++) await chain.GenerateJsonAsync(Request);
        Assert.Equal(3, primary.CallCount);

        // Cuarta llamada: el circuito de gemini está abierto — no se le llama.
        await chain.GenerateJsonAsync(Request);
        Assert.Equal(3, primary.CallCount);
    }

    [Fact]
    public async Task PreCancelledToken_ThrowsWithoutCallingAnyProvider()
    {
        var primary = StubLlmClient.Succeeding("gemini", "{\"ok\":true}");
        var secondary = StubLlmClient.Succeeding("openai", "{\"ok\":true}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Chain(primary, secondary).GenerateJsonAsync(Request, cts.Token));

        // La cancelación del caller no debe quemar intentos de ningún provider.
        Assert.Equal(0, primary.CallCount);
        Assert.Equal(0, secondary.CallCount);
    }

    [Fact]
    public async Task EmptyChain_ReturnsMissingKeyDiagnostics()
    {
        var response = await Chain().GenerateJsonAsync(Request);

        Assert.False(response.Succeeded);
        Assert.Equal("missing_key", response.Diagnostics.ErrorCode);
        Assert.Equal("none", response.Diagnostics.Provider);
    }
}
