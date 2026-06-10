using System.Collections.Concurrent;

namespace LocalList.API.NET.Shared.AI.Llm;

/// <summary>
/// Circuit breaker mínimo a nivel de cadena de fallback: tras N fallos consecutivos
/// el provider se salta durante un cooldown, evitando pagar su timeout en cada turno
/// mientras está caído. Complementa (no sustituye) al resilience handler por-HttpClient.
/// Singleton; thread-safe.
/// </summary>
public sealed class LlmProviderHealthRegistry(TimeProvider clock)
{
    private const int FailureThreshold = 3;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    private sealed record State(int ConsecutiveFailures, DateTimeOffset OpenUntil);

    private readonly ConcurrentDictionary<string, State> _states = new();

    public bool IsOpen(string provider)
    {
        if (!_states.TryGetValue(provider, out var s)) return false;
        if (s.ConsecutiveFailures < FailureThreshold) return false;
        if (clock.GetUtcNow() >= s.OpenUntil)
        {
            // Cooldown vencido: half-open — permitir un intento (el resultado decide).
            return false;
        }
        return true;
    }

    public void RecordFailure(string provider)
    {
        _states.AddOrUpdate(
            provider,
            _ => new State(1, DateTimeOffset.MinValue),
            (_, s) =>
            {
                var failures = s.ConsecutiveFailures + 1;
                var openUntil = failures >= FailureThreshold
                    ? clock.GetUtcNow().Add(Cooldown)
                    : s.OpenUntil;
                return new State(failures, openUntil);
            });
    }

    public void RecordSuccess(string provider) => _states.TryRemove(provider, out _);
}
