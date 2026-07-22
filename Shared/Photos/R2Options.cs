namespace LocalList.API.NET.Shared.Photos;

/// <summary>
/// Config binding de la sección "R2" (Cloudflare R2 vía S3 API).
/// Sin credenciales (<see cref="AccountId"/> / <see cref="AccessKeyId"/> /
/// <see cref="SecretAccessKey"/>) el rehost de fotos degrada graceful
/// (se persiste la URL original + warning), patrón Mapbox/Klaviyo.
/// </summary>
public class R2Options
{
    public const string SectionName = "R2";

    public string? AccountId { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }

    /// <summary>Bucket de destino. Default: el bucket existente del pipeline manual (Scripts/upload-photos-to-r2.js).</summary>
    public string Bucket { get; set; } = "locallist-images";

    /// <summary>
    /// Base pública desde la que se sirven los objetos. Default: el dominio r2.dev público
    /// del bucket (mismo default que Scripts/upload-photos-to-r2.js — es información pública).
    /// </summary>
    public string PublicUrl { get; set; } = "https://pub-7f09e69b5b644703825b6068a05dee8f.r2.dev";

    /// <summary>
    /// Allowlist de hosts desde los que el rehost puede descargar (defensa SSRF —
    /// <see cref="PhotoUrls.IsAllowedSource"/>). Soporta exacto y wildcard <c>*.sufijo</c>.
    /// Ampliable por config (<c>R2__AllowedPhotoSourceHosts__0=...</c>) si aparece una fuente
    /// nueva legítima — el backfill reporta los hosts no migrados en <c>otherDomains</c>.
    /// </summary>
    public List<string> AllowedPhotoSourceHosts { get; set; } =
    [
        "places.googleapis.com",
        "*.googleusercontent.com",
        "*.ggpht.com",
        "wanderlog.com",
        "*.wanderlog.com",
    ];
}
