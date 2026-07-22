namespace LocalList.API.NET.Shared.Photos;

/// <summary>
/// Helpers puros para clasificar y sanear URLs de fotos.
/// Punto único de conocimiento sobre "qué es una URL de Google Places" — si la decisión
/// de ToS pendiente (flag a Pablo) obliga a tratar las fotos de Google distinto por fuente,
/// el cambio se concentra aquí.
/// </summary>
public static class PhotoUrls
{
    public const string GooglePlacesHost = "places.googleapis.com";

    /// <summary>Buckets del censo por dominio que reporta POST /admin/places/backfill-photos.</summary>
    public const string BucketGoogle = "places.googleapis.com";
    public const string BucketR2 = "r2.dev";
    public const string BucketWanderlog = "wanderlog.com";
    public const string BucketOther = "other";

    /// <summary>
    /// True si la URL lleva un query param <c>key</c> (la API key de Google Places).
    /// Ninguna URL así debe salir por la API pública ni persistirse tras un rehost con config.
    /// </summary>
    public static bool ContainsApiKey(string url)
    {
        var qIndex = url.IndexOf('?');
        if (qIndex < 0 || qIndex == url.Length - 1) return false;

        foreach (var pair in url[(qIndex + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            var name = eq < 0 ? pair : pair[..eq];
            if (name.Equals("key", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool IsGooglePlacesUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.Equals(GooglePlacesHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Defensa a nivel DTO: filtra cualquier URL con <c>key=</c> antes de que salga por la API
    /// pública. Devuelve la misma instancia si no hay nada que filtrar (caso común, sin alloc).
    /// </summary>
    public static List<string>? Sanitize(List<string>? photos)
    {
        if (photos is not { Count: > 0 }) return photos;
        return photos.Any(ContainsApiKey)
            ? photos.Where(u => !ContainsApiKey(u)).ToList()
            : photos;
    }

    /// <summary>
    /// Valida que una URL sea una fuente aceptable para el rehost server-side (defensa SSRF):
    /// https estricto, sin IP literals (privadas o no — los hosts legítimos siempre son DNS),
    /// sin localhost, y host dentro de la allowlist (<paramref name="allowedHostPatterns"/>,
    /// exacto o wildcard <c>*.sufijo</c>). El GET del rehost parte de input admin (bulk import),
    /// pero un admin comprometido o un JSON pegado sin revisar no debe poder apuntar el
    /// servidor a metadata endpoints ni a la red interna.
    /// </summary>
    public static bool IsAllowedSource(string url, IReadOnlyCollection<string> allowedHostPatterns)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        // IP literals nunca: las fuentes legítimas (Google/CDNs) siempre van por hostname.
        // Esto bloquea de paso 169.254.169.254, rangos privados y [::1] sin lista de rangos.
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6) return false;

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var pattern in allowedHostPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = pattern[1..]; // ".dominio.com"
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    host.Length > suffix.Length)
                    return true;
            }
            else if (host.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Clasifica una URL en los buckets del censo por dominio:
    /// places.googleapis.com / r2.dev / wanderlog.com / other.
    /// <paramref name="r2PublicHost"/> permite reconocer un dominio público custom
    /// (p. ej. images.locallist.ai) además de *.r2.dev.
    /// </summary>
    public static string DomainBucket(string url, string? r2PublicHost = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return BucketOther;

        var host = uri.Host;
        if (host.Equals(GooglePlacesHost, StringComparison.OrdinalIgnoreCase))
            return BucketGoogle;
        if (host.EndsWith(".r2.dev", StringComparison.OrdinalIgnoreCase) ||
            (r2PublicHost is not null && host.Equals(r2PublicHost, StringComparison.OrdinalIgnoreCase)))
            return BucketR2;
        if (host.Equals("wanderlog.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".wanderlog.com", StringComparison.OrdinalIgnoreCase))
            return BucketWanderlog;
        return BucketOther;
    }
}
