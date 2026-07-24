namespace LocalList.API.NET.Features.Places.Photos;

/// <summary>
/// Validación de host compartida por TODO endpoint que emite un 302 hacia el
/// <c>photoUri</c> efímero devuelto por <see cref="IPlacePhotoService"/> (T1:
/// <c>PlacePhotosController</c> público; T3: el preview admin-authed de
/// <c>AdminPlacesController</c>). Defensa en profundidad: el photoUri viene de Google sobre
/// TLS, pero validamos el host antes de redirigir para no convertirnos en un open redirector
/// si esa fuente cambiara. No dupliques esta comprobación, referencia esta clase.
/// </summary>
public static class GooglePhotoHostValidator
{
    /// <summary>
    /// True solo si <paramref name="photoUri"/> es una URL https absoluta cuyo host es
    /// exactamente <c>googleusercontent.com</c> o un subdominio (<c>*.googleusercontent.com</c>).
    /// Compara el <see cref="Uri.Host"/> parseado, no substrings: un host como
    /// <c>googleusercontent.com.attacker.example</c> NO termina en <c>.googleusercontent.com</c>
    /// y por tanto se rechaza (no evade la allowlist).
    /// </summary>
    public static bool IsAllowedGooglePhotoHost(string photoUri)
    {
        if (!Uri.TryCreate(photoUri, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var host = uri.Host;
        return host.Equals("googleusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase);
    }
}
