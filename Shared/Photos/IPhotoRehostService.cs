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

public readonly record struct PhotoRehostResult(PhotoRehostOutcome Outcome, string Url);

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
