namespace LocalList.API.NET.Features.Admin.Places;

/// <summary>
/// Punto único de síntesis de las URLs de preview de fotos que el ADMIN ve durante el import
/// (search / resolve-from-url), ANTES de que el Place exista en DB, por lo que el proxy
/// público de T1 (<c>/places/{id}/photos/0</c>, que necesita un Place.Id interno) no aplica
/// todavía. En su lugar se referencia el endpoint admin-authed de T3
/// (<c>GET /admin/places/photo-preview?googlePlaceId=X&amp;index=I</c>), que resuelve la key
/// server-side reutilizando <see cref="LocalList.API.NET.Features.Places.Photos.IPlacePhotoService"/>
/// de T1 y nunca la expone al navegador del admin.
///
/// Ningún caller de <see cref="GooglePlacesService"/> debe volver a construir una URL
/// <c>places.googleapis.com</c> con la key en el query string: ese punto de síntesis vive
/// solo aquí.
/// </summary>
public static class AdminPlacePhotoPreviewUrls
{
    /// <summary>
    /// Genera hasta <paramref name="count"/> URLs de preview (índices 0..count-1) para un sitio
    /// de Google identificado por <paramref name="googlePlaceId"/>.
    /// </summary>
    public static List<string> ForGooglePlace(string googlePlaceId, int count, string? publicBaseUrl) =>
        Enumerable.Range(0, count)
            .Select(index => Preview(googlePlaceId, index, publicBaseUrl))
            .ToList();

    /// <summary>
    /// URL pública del preview admin-authed para la foto <paramref name="index"/> del sitio de
    /// Google <paramref name="googlePlaceId"/>. Con <c>Api:PublicBaseUrl</c> configurada
    /// (prod/staging) devuelve una URL absoluta; vacía/sin configurar (dev local) devuelve una
    /// ruta relativa que el admin resuelve contra su propia base de API.
    /// </summary>
    public static string Preview(string googlePlaceId, int index, string? publicBaseUrl)
    {
        var path = $"/admin/places/photo-preview?googlePlaceId={Uri.EscapeDataString(googlePlaceId)}&index={index}";
        return string.IsNullOrWhiteSpace(publicBaseUrl) ? path : publicBaseUrl.TrimEnd('/') + path;
    }
}
