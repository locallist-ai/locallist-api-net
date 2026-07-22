namespace LocalList.API.NET.Shared.Photos;

/// <summary>
/// Circuit breaker POR-REQUEST del rehost de fotos en la INGESTA (create / bulk /
/// import-from-urls / PATCH con <c>photos</c>). Espejo del breaker del backfill
/// (BackfillPhotosCoordinator / UploadFailureAbortThreshold), pero con política
/// distinta: en ingesta NUNCA se aborta la request — se degrada a "sin foto" y se sigue.
///
/// <para>Motivación (G2): el timeout de 10s (M4) acota UN upload y
/// <see cref="PhotoRehostService.RehostAsync"/> nunca propaga un fallo de R2 — pero sin
/// presupuesto agregado, un import de N places con R2 colgado (acepta TCP, no responde)
/// gasta ~10s por foto hasta agotar el proxy de Railway (40s); en ese instante el ct de la
/// request se cancela y el <c>OperationCanceledException</c> SÍ propaga → la request revienta
/// y, con el commit por chunks (M5), lo ya rehosteado se pierde mientras Google ya se
/// facturó. El breaker corta ese sangrado: tras <see cref="DefaultThreshold"/> fallos
/// consecutivos de UPLOAD (culpa de R2, no de la fuente), el resto de la request deja de
/// intentar rehost — no re-descarga de Google (no re-facturar) y persiste los places sin
/// foto (URLs con <c>key=</c> descartadas) o con su URL original (sin key, para el
/// backfill).</para>
///
/// Un upload OK resetea el contador. Instancia NUEVA por request (nunca compartida entre
/// requests): su estado no debe filtrarse. En un bulk/import es UNA instancia para todos los
/// places del request (el presupuesto es agregado); en create/PATCH de un solo place es
/// trivial pero uniforme.
/// </summary>
public sealed class IngestPhotoBreaker
{
    /// <summary>Fallos consecutivos de upload que abren el circuito (mismo umbral que el backfill).</summary>
    public const int DefaultThreshold = 3;

    private readonly int _threshold;
    private int _consecutiveUploadFailures;

    public IngestPhotoBreaker(int threshold = DefaultThreshold) => _threshold = threshold;

    /// <summary>
    /// True cuando <see cref="DefaultThreshold"/> uploads consecutivos han fallado: el caller
    /// debe dejar de intentar rehost en el resto de la request y degradar a "sin foto".
    /// </summary>
    public bool IsOpen => _consecutiveUploadFailures >= _threshold;

    /// <summary>Registra un fallo de upload a R2 (stage <see cref="PhotoRehostFailureStage.Upload"/>).</summary>
    public void RecordUploadFailure() => _consecutiveUploadFailures++;

    /// <summary>Un upload salió bien — el circuito se cierra (streak a cero).</summary>
    public void RecordUploadSuccess() => _consecutiveUploadFailures = 0;
}
