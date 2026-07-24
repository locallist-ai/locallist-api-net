namespace LocalList.API.NET.Features.Places.Photos;

/// <summary>
/// Circuit breaker de presupuesto diario GLOBAL para las llamadas <c>/media</c> de Google
/// Places (el único SKU de pago del proxy de fotos, ~$0.007 cada una). Cuenta las llamadas
/// servidas en el día UTC en curso y, al alcanzar el cap, deja de autorizar nuevas llamadas
/// hasta el reset del día siguiente. El endpoint degrada a 404 (gradiente) en vez de 500.
///
/// Estado in-process (un contador + el día vigente) → NO coordina entre réplicas. Con 2+
/// réplicas el cap efectivo se multiplica por el número de réplicas. Documentado en
/// "Scaling invariants" del CLAUDE.md del backend junto al resto de estado in-memory.
///
/// Se registra como singleton. El reset por día se resuelve de forma perezosa (lazy) en
/// cada <see cref="TryAcquire"/> comparando el día UTC actual (vía <see cref="TimeProvider"/>)
/// con el último día contado — no hay timer de fondo.
/// </summary>
public sealed class PhotoBudgetCounter
{
    /// <summary>Cap por defecto (~$70/día a ~$0.007 la llamada). Decisión Pablo.</summary>
    public const int DefaultDailyCap = 10000;

    private readonly TimeProvider _clock;
    private readonly int _dailyCap;
    private readonly object _gate = new();

    private DateOnly _day;
    private int _count;

    public PhotoBudgetCounter(TimeProvider clock, IConfiguration configuration)
    {
        _clock = clock;
        _dailyCap = configuration.GetValue<int?>("GooglePlaces:PhotoDailyBudgetCap")
                    ?? DefaultDailyCap;
    }

    /// <summary>
    /// Intenta reservar una llamada <c>/media</c> del presupuesto de hoy. Devuelve
    /// <c>true</c> si queda presupuesto (e incrementa el contador), <c>false</c> si el cap
    /// del día ya se alcanzó. Reinicia el contador al cambiar el día UTC. Thread-safe.
    /// </summary>
    public bool TryAcquire()
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        lock (_gate)
        {
            if (today != _day)
            {
                _day = today;
                _count = 0;
            }

            if (_count >= _dailyCap)
                return false;

            _count++;
            return true;
        }
    }
}
