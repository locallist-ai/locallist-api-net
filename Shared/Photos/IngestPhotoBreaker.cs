using System.Diagnostics;

namespace LocalList.API.NET.Shared.Photos;

/// <summary>
/// Circuit breaker POR-REQUEST del rehost de fotos en la INGESTA (create / bulk /
/// import-from-urls / PATCH con <c>photos</c>). Espejo del breaker del backfill
/// (BackfillPhotosCoordinator / UploadFailureAbortThreshold), pero con política
/// distinta: en ingesta NUNCA se aborta la request — se degrada a "sin foto" y se sigue.
///
/// <para>Motivación (G2 / ronda 3): el timeout de 10s (M4) acota UN upload y
/// <see cref="PhotoRehostService.RehostAsync"/> nunca propaga un fallo de R2 — pero el conteo
/// de intentos NO acota TIEMPO. Contra un R2 COLGADO (acepta TCP, no responde) cada upload
/// gasta ~Timeout×retry (~20s); ~2 uploads ≈ 40s = deadline del proxy de Railway ANTES de que
/// el 3er fallo abra el breaker. En ese instante el ct del caller se cancela y el
/// <c>OperationCanceledException</c> SÍ propaga → la request revienta y, con el commit por
/// chunks (M5), lo ya rehosteado se pierde mientras Google ya se facturó.</para>
///
/// <para>Doble defensa, por eso el breaker es <b>por-intentos Y por-tiempo</b>:</para>
/// <list type="bullet">
///   <item><b>Intentos</b>: tras <see cref="DefaultThreshold"/> fallos consecutivos de UPLOAD
///   (culpa de R2, no de la fuente) el resto de la request deja de intentar rehost.</item>
///   <item><b>Wall-clock</b> (<see cref="DefaultBudget"/>): el presupuesto agregado de la
///   request. Es una <em>fracción</em> del deadline del proxy — al agotarse, <see cref="IsOpen"/>
///   pasa a true aunque no se hayan acumulado 3 fallos, de modo que se corta CON MARGEN para
///   commitear el place antes de que el proxy cancele el ct. <see cref="Remaining"/> acota
///   además CADA upload individual (<see cref="PhotoRehostService.RehostForIngestAsync"/> lo
///   pasa a un linked CTS) para que un solo cuelgue no rebase el presupuesto.</item>
/// </list>
///
/// El resto de places degrada a "sin foto" (URLs con <c>key=</c> descartadas) o a su URL
/// original (sin key, para el backfill). Un upload OK resetea el contador de intentos (el
/// presupuesto de tiempo NO se resetea — es de la request entera). Instancia NUEVA por request
/// (nunca compartida): su estado y su reloj no deben filtrarse. En un bulk/import es UNA
/// instancia para todos los places (presupuesto agregado); en create/PATCH de un solo place es
/// trivial pero uniforme.
/// </summary>
public sealed class IngestPhotoBreaker
{
    /// <summary>Fallos consecutivos de upload que abren el circuito (mismo umbral que el backfill).</summary>
    public const int DefaultThreshold = 3;

    /// <summary>
    /// Presupuesto de wall-clock por defecto de la request. Fracción del deadline del proxy de
    /// Railway (~40s): al agotarse se deja de intentar rehost con margen suficiente para
    /// commitear el place. Configurable vía <c>R2Options.IngestRehostBudget</c>.
    /// </summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(25);

    private readonly int _threshold;
    private readonly TimeSpan _budget;
    private readonly long _startTimestamp;
    private int _consecutiveUploadFailures;
    private bool _budgetTripped;

    public IngestPhotoBreaker(int threshold = DefaultThreshold, TimeSpan? budget = null)
    {
        _threshold = threshold;
        _budget = budget is { } b && b > TimeSpan.Zero ? b : DefaultBudget;
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Tiempo transcurrido desde que arrancó la request (reloj monotónico).</summary>
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_startTimestamp);

    /// <summary>Presupuesto de wall-clock restante (nunca negativo). Acota cada upload individual.</summary>
    public TimeSpan Remaining
    {
        get
        {
            var left = _budget - Elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>True si el presupuesto de wall-clock de la request se agotó (por reloj o por latch).</summary>
    public bool BudgetExhausted => _budgetTripped || Elapsed >= _budget;

    /// <summary>
    /// True cuando <see cref="DefaultThreshold"/> uploads consecutivos han fallado O el
    /// presupuesto de wall-clock de la request se ha agotado: el caller debe dejar de intentar
    /// rehost en el resto de la request y degradar a "sin foto".
    /// </summary>
    public bool IsOpen => _consecutiveUploadFailures >= _threshold || BudgetExhausted;

    /// <summary>
    /// Latch: marca el presupuesto como agotado de forma DEFINITIVA. Lo llama
    /// <see cref="PhotoRehostService.RehostForIngestAsync"/> al cortar un upload por el
    /// presupuesto (linked CTS) — así el resto de places de la request degradan de inmediato y
    /// de forma DETERMINISTA, sin depender de si <see cref="Elapsed"/> cae justo en el borde del
    /// presupuesto (evita que un place cuele una descarga a Google en la frontera).
    /// </summary>
    public void TripBudget() => _budgetTripped = true;

    /// <summary>Registra un fallo de upload a R2 (stage <see cref="PhotoRehostFailureStage.Upload"/>).</summary>
    public void RecordUploadFailure() => _consecutiveUploadFailures++;

    /// <summary>Un upload salió bien — el contador de intentos se cierra (streak a cero; el reloj no).</summary>
    public void RecordUploadSuccess() => _consecutiveUploadFailures = 0;
}
