using System.Collections.Concurrent;

namespace LocalList.API.NET.Shared.Photos;

/// <summary>
/// Estado in-process del backfill de fotos (singleton). Dos responsabilidades:
///
/// 1. <b>Lock de ejecución</b> (<see cref="TryAcquireRun"/>): solo un barrido de
///    POST /admin/places/backfill-photos a la vez. Ejecuciones concurrentes duplicarían
///    descargas de Google (doble facturación) y escrituras a R2. El segundo caller recibe
///    409 "backfill_already_running".
///
/// 2. <b>Deferral de places fallidos</b> (<see cref="RecordFailure"/> / <see cref="IsDeferred"/>):
///    un place cuyo rehost falla por causa de la FUENTE (404 de Google, host no allowlisted,
///    imagen corrupta) entra en backoff exponencial y se excluye de los siguientes barridos
///    hasta que expire. Sin esto, N ≥ limit places con fallo persistente monopolizan la primera
///    página (OrderBy CreatedAt) y los places migrables de detrás no se procesan nunca
///    (livelock del runbook "hasta remainingPlaces=0"). Los fallos de UPLOAD a R2 no difieren
///    el place (no es culpa suyo) — cortan el barrido entero vía circuit breaker en el controller.
///
/// SCALING INVARIANT: todo es in-process (SemaphoreSlim + ConcurrentDictionary). Con la única
/// réplica de Railway es correcto; con 2+ réplicas haría falta un lock distribuido (p. ej.
/// advisory lock de Postgres) y estado compartido. Documentado en CLAUDE.md → Scaling invariants.
/// Un restart pierde el estado de deferral — inocuo: el primer barrido reintenta los fallidos,
/// vuelven al backoff y el siguiente barrido avanza igual.
/// </summary>
public sealed class BackfillPhotosCoordinator
{
    /// <summary>Backoff base tras el primer fallo; se duplica por intento (cap <see cref="MaxBackoff"/>).</summary>
    public static readonly TimeSpan BaseBackoff = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, FailureState> _failures = new();

    private readonly record struct FailureState(int Attempts, DateTimeOffset DeferredUntil);

    /// <summary>
    /// Intenta adquirir el lock del barrido sin esperar. Devuelve false si ya hay un
    /// backfill en curso — el caller debe responder 409, nunca encolarse (duplicaría coste).
    /// </summary>
    public bool TryAcquireRun() => _runGate.Wait(TimeSpan.Zero);

    public void ReleaseRun() => _runGate.Release();

    /// <summary>True si el place está en backoff y debe saltarse este barrido.</summary>
    public bool IsDeferred(Guid placeId, DateTimeOffset now) =>
        _failures.TryGetValue(placeId, out var s) && s.DeferredUntil > now;

    /// <summary>
    /// Registra un fallo atribuible a la fuente (download/decode/blocked) y difiere el place
    /// con backoff exponencial: 5min, 10min, 20min… cap 6h.
    /// </summary>
    public void RecordFailure(Guid placeId, DateTimeOffset now)
    {
        _failures.AddOrUpdate(
            placeId,
            _ => new FailureState(1, now + BaseBackoff),
            (_, prev) =>
            {
                var attempts = prev.Attempts + 1;
                var backoffTicks = BaseBackoff.Ticks * (1L << Math.Min(attempts - 1, 10));
                var backoff = TimeSpan.FromTicks(Math.Min(backoffTicks, MaxBackoff.Ticks));
                return new FailureState(attempts, now + backoff);
            });
    }

    /// <summary>El place migró por completo — sale del backoff.</summary>
    public void RecordSuccess(Guid placeId) => _failures.TryRemove(placeId, out _);

    /// <summary>Nº de places de <paramref name="candidateIds"/> actualmente en backoff.</summary>
    public int CountDeferred(IEnumerable<Guid> candidateIds, DateTimeOffset now) =>
        candidateIds.Count(id => IsDeferred(id, now));

    /// <summary>Solo para tests: limpia deferrals (el lock no se toca — debe estar liberado).</summary>
    public void Reset() => _failures.Clear();
}
