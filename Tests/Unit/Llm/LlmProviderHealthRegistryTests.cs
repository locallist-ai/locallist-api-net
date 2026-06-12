using LocalList.API.NET.Shared.AI.Llm;
using Microsoft.Extensions.Time.Testing;

namespace LocalList.API.Tests.Unit.Llm;

public class LlmProviderHealthRegistryTests
{
    [Fact]
    public void IsOpen_NoFailures_ReturnsFalse()
    {
        var registry = new LlmProviderHealthRegistry(new FakeTimeProvider());
        Assert.False(registry.IsOpen("gemini"));
    }

    [Fact]
    public void IsOpen_BelowThreshold_ReturnsFalse()
    {
        var registry = new LlmProviderHealthRegistry(new FakeTimeProvider());
        registry.RecordFailure("gemini");
        registry.RecordFailure("gemini");
        Assert.False(registry.IsOpen("gemini"));
    }

    [Fact]
    public void IsOpen_ThreeConsecutiveFailures_OpensCircuit()
    {
        var registry = new LlmProviderHealthRegistry(new FakeTimeProvider());
        registry.RecordFailure("gemini");
        registry.RecordFailure("gemini");
        registry.RecordFailure("gemini");
        Assert.True(registry.IsOpen("gemini"));
    }

    [Fact]
    public void IsOpen_AfterCooldown_AllowsHalfOpenAttempt()
    {
        var time = new FakeTimeProvider();
        var registry = new LlmProviderHealthRegistry(time);
        for (var i = 0; i < 3; i++) registry.RecordFailure("gemini");
        Assert.True(registry.IsOpen("gemini"));

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(registry.IsOpen("gemini"));
    }

    [Fact]
    public void HalfOpen_FailedProbe_ReopensCircuitForAnotherCooldown()
    {
        var time = new FakeTimeProvider();
        var registry = new LlmProviderHealthRegistry(time);
        for (var i = 0; i < 3; i++) registry.RecordFailure("gemini");

        // Cooldown vencido: half-open, se permite un intento de sondeo.
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(registry.IsOpen("gemini"));

        // El intento half-open falla → el circuito reabre otros 60s desde ahora.
        registry.RecordFailure("gemini");
        Assert.True(registry.IsOpen("gemini"));

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(registry.IsOpen("gemini")); // dentro del nuevo cooldown sigue abierto

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(registry.IsOpen("gemini")); // nuevo half-open al vencer el segundo cooldown
    }

    [Fact]
    public void HalfOpen_SuccessfulProbe_ClosesCircuit()
    {
        var time = new FakeTimeProvider();
        var registry = new LlmProviderHealthRegistry(time);
        for (var i = 0; i < 3; i++) registry.RecordFailure("gemini");

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(registry.IsOpen("gemini"));

        // El sondeo half-open responde bien → circuito cerrado y contador a cero.
        registry.RecordSuccess("gemini");
        Assert.False(registry.IsOpen("gemini"));
        registry.RecordFailure("gemini");
        Assert.False(registry.IsOpen("gemini")); // 1 fallo < umbral: el historial previo no cuenta
    }

    [Fact]
    public void RecordSuccess_ResetsFailureCount()
    {
        var registry = new LlmProviderHealthRegistry(new FakeTimeProvider());
        registry.RecordFailure("gemini");
        registry.RecordFailure("gemini");
        registry.RecordSuccess("gemini");
        registry.RecordFailure("gemini");
        Assert.False(registry.IsOpen("gemini"));
    }

    [Fact]
    public void Circuits_AreIndependentPerProvider()
    {
        var registry = new LlmProviderHealthRegistry(new FakeTimeProvider());
        for (var i = 0; i < 3; i++) registry.RecordFailure("gemini");
        Assert.True(registry.IsOpen("gemini"));
        Assert.False(registry.IsOpen("openai"));
    }
}
