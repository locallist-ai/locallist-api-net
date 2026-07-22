namespace LocalList.API.NET.Shared.Photos;

public enum PhotoRehostOutcome
{
    /// <summary>La URL ya apunta al host público de R2 — no se toca.</summary>
    AlreadyHosted,

    /// <summary>Sin credenciales R2 — degradación graceful, se conserva la URL original.</summary>
    NotConfigured,

    /// <summary>Descargada, reencodada a webp (máx 1200px) y subida a R2.</summary>
    Rehosted,

    /// <summary>Descarga/decode/upload falló — <see cref="PhotoRehostResult.Url"/> conserva la original.</summary>
    Failed,
}

/// <summary>
/// En qué fase falló un rehost (<see cref="PhotoRehostOutcome.Failed"/>). Los callers la usan
/// para decidir política de coste: un fallo de <see cref="Upload"/> significa que la descarga
/// de Google YA se facturó sin resultado — el backfill corta el barrido (circuit breaker)
/// para no seguir quemando dinero contra un R2 caído. Un fallo de <see cref="Blocked"/> o
/// <see cref="Download"/> no facturó nada útil pero es atribuible a la fuente, no a R2.
/// </summary>
public enum PhotoRehostFailureStage
{
    None,

    /// <summary>La URL no pasó la validación de fuente (allowlist SSRF / esquema / IP literal).</summary>
    Blocked,

    /// <summary>El GET a la fuente falló (4xx/5xx, timeout, content-type no imagen, tamaño).</summary>
    Download,

    /// <summary>Los bytes no decodifican como imagen o exceden los límites de dimensiones.</summary>
    Decode,

    /// <summary>La subida a R2 falló — la descarga (facturable) ya se hizo.</summary>
    Upload,
}

public readonly record struct PhotoRehostResult(
    PhotoRehostOutcome Outcome,
    string Url,
    PhotoRehostFailureStage FailureStage = PhotoRehostFailureStage.None);

public interface IPhotoRehostService
{
    /// <summary>False cuando faltan credenciales R2 — el rehost degrada graceful.</summary>
    bool IsConfigured { get; }

    /// <summary>Host del dominio público de R2 configurado (para clasificar URLs), o null.</summary>
    string? PublicHost { get; }

    /// <summary>
    /// Rehostea una foto: descarga server-side, reencoda (webp, máx 1200px de ancho) y sube
    /// a R2. <paramref name="keyHint"/> (típicamente el nombre del place) se slugifica para
    /// el key del objeto. Nunca lanza por fallos de red/imagen — devuelve <see cref="PhotoRehostOutcome.Failed"/>.
    /// </summary>
    Task<PhotoRehostResult> RehostAsync(string sourceUrl, string keyHint, CancellationToken ct);

    /// <summary>
    /// Variante para ingesta: rehostea todas las URLs y aplica la política de persistencia —
    /// con R2 configurado NUNCA devuelve una URL de Google con key (si el rehost falla, la
    /// foto se descarta); sin config conserva las originales (graceful). Devuelve null si el
    /// resultado queda vacío.
    /// </summary>
    Task<List<string>?> RehostForIngestAsync(IReadOnlyList<string>? urls, string keyHint, CancellationToken ct);
}
