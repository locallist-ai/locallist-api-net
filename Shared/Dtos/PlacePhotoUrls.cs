namespace LocalList.API.NET.Shared.Dtos;

/// <summary>
/// Punto único de síntesis de las URLs de fotos que salen al cliente para un Place. Ningún
/// DTO debe volver a emitir directamente una URL <c>places.googleapis.com</c> (lleva la API
/// key en el query string): si el Place tiene <c>GooglePlaceId</c>, se sintetiza la URL del
/// proxy runtime (T1: <c>GET /places/{id}/photos/0</c>), que resuelve la key server-side y
/// nunca la expone. Decisión de producto: SOLO hero (index 0), nunca la galería completa.
/// </summary>
public static class PlacePhotoUrls
{
    /// <summary>
    /// Resuelve <c>Photos</c>/<c>photoSource</c> para un Place dado:
    /// - <c>GooglePlaceId</c> presente -> proxy sintetizado, source "google" (ignora
    ///   cualquier URL de Google guardada en <paramref name="storedPhotos"/>: nunca se
    ///   reemite una guardada, aunque exista).
    /// - Si no, URLs externas no-Google guardadas -> tal cual, source "external". Se filtra
    ///   defensivamente cualquier host <c>googleapis.com</c> que pudiera colarse aquí (p.ej.
    ///   datos legacy pre-T4), para que este punto único de síntesis jamás reemita una key
    ///   aunque el dato de origen esté sucio.
    /// - Si no hay ninguna de las dos -> sin fotos, source null.
    /// </summary>
    public static (List<string> Photos, string? PhotoSource) Resolve(
        Guid placeId, string? googlePlaceId, List<string>? storedPhotos, string? publicBaseUrl)
    {
        if (!string.IsNullOrEmpty(googlePlaceId))
            return ([Hero(placeId, publicBaseUrl)], "google");

        var external = (storedPhotos ?? [])
            .Where(url => !string.IsNullOrWhiteSpace(url) && !IsGoogleHostedUrl(url))
            .ToList();

        return external.Count > 0 ? (external, "external") : ([], null);
    }

    /// <summary>
    /// URL pública del proxy de la foto hero (index 0). Con <c>Api:PublicBaseUrl</c>
    /// configurada (prod/staging) devuelve una URL absoluta; vacía/sin configurar (dev local
    /// por defecto) devuelve una ruta relativa <c>/places/{id}/photos/0</c>. La app la
    /// resuelve contra su propia <c>EXPO_PUBLIC_API_URL</c>, así que el fallback es
    /// perfectamente servible y no requiere configurar nada para desarrollar en local.
    /// </summary>
    public static string Hero(Guid placeId, string? publicBaseUrl)
    {
        var path = $"/places/{placeId}/photos/0";
        return string.IsNullOrWhiteSpace(publicBaseUrl) ? path : publicBaseUrl.TrimEnd('/') + path;
    }

    /// <summary>
    /// True si la URL apunta a un host de Google Places (las que llevan <c>key=</c> en el
    /// query string). Comprobación defensiva: en teoría este branch solo se alcanza sin
    /// <c>GooglePlaceId</c>, pero no confiamos ciegamente en la limpieza del dato guardado.
    /// </summary>
    private static bool IsGoogleHostedUrl(string url) =>
        url.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True si la URL es el preview admin-authed de T3 (<c>/admin/places/photo-preview</c>).
    /// Nunca debe acabar en <c>Place.Photos</c>: requiere auth de admin, así que un cliente
    /// normal de la app recibiría 401/403 al intentar cargarla.
    /// </summary>
    private static bool IsAdminPreviewUrl(string url) =>
        url.Contains("/admin/places/photo-preview", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Higiene de escritura (T3, defensa en profundidad): elimina de <paramref name="photos"/>
    /// cualquier entrada que no sea una URL directamente servible por un cliente público:
    /// ni URLs de Google con key, ni referencias al preview admin-authed. Se aplica en TODAS
    /// las rutas de escritura de <c>Place.Photos</c> (creación directa, bulk, update): aunque
    /// un caller intente colar una de esas URLs (p. ej. copy-paste desde una respuesta de
    /// import), nunca llega a persistirse en Postgres. Devuelve null si no queda nada servible
    /// (nunca una lista vacía), para no romper la semántica "sin fotos" = null del resto del
    /// código.
    /// </summary>
    public static List<string>? SanitizeForStorage(List<string>? photos)
    {
        if (photos is null) return null;

        var clean = photos
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Where(url => !IsGoogleHostedUrl(url))
            .Where(url => !IsAdminPreviewUrl(url))
            .ToList();

        return clean.Count > 0 ? clean : null;
    }
}
