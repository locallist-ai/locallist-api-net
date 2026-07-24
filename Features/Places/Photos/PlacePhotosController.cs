using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Data;

namespace LocalList.API.NET.Features.Places.Photos;

/// <summary>
/// Proxy de fotos de Google runtime-only. La app carga
/// <c>&lt;Image src="{PublicBaseUrl}/places/{id}/photos/0" /&gt;</c> y el backend resuelve
/// server-side el <c>photoUri</c> efímero del CDN de Google, respondiendo 302 hacia él. Así
/// la API key de Google NUNCA sale al cliente y no se viola el ToS (no se almacena nada).
///
/// [AllowAnonymous] a propósito: las etiquetas <c>&lt;Image&gt;</c> no adjuntan el header de
/// auth. Toda condición de fallo (place inexistente/sin GooglePlaceId, index fuera de rango,
/// sin key, presupuesto agotado, error de Google) degrada a 404 → la app pinta un gradiente.
/// Nunca 500 (rompería la Image).
/// </summary>
[ApiController]
[Route("places")]
[AllowAnonymous]
public sealed class PlacePhotosController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly IPlacePhotoService _photos;

    public PlacePhotosController(LocalListDbContext db, IPlacePhotoService photos)
    {
        _db = db;
        _photos = photos;
    }

    [HttpGet("{id:guid}/photos/{index:int}")]
    [EnableRateLimiting("PhotoLimit")]
    public async Task<IActionResult> GetPhoto(Guid id, int index, CancellationToken ct)
    {
        // no-store en TODA respuesta (incluidos los 404): un 404 transitorio (presupuesto
        // agotado, error puntual de Google) no debe quedar cacheado en un CDN/navegador y
        // seguir suprimiendo la foto tras el reset diario. El photoUri del 302 además es
        // efímero, así que ningún intermediario debe cachearlo (regla ToS de no-caché).
        Response.Headers.CacheControl = "no-store";

        // Solo el GooglePlaceId del place — sin cargar la entidad entera.
        var googlePlaceId = await _db.Places.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => p.GooglePlaceId)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrEmpty(googlePlaceId))
            return NotFound();

        var photoUri = await _photos.ResolvePhotoUriAsync(googlePlaceId, index, ct);
        if (string.IsNullOrEmpty(photoUri))
            return NotFound();

        // Defensa en profundidad: solo redirigimos a hosts del CDN de Google. El photoUri
        // viene de Google sobre TLS, pero validamos el host antes de emitir el 302 para no
        // convertirnos en un redirector abierto si esa fuente cambiara. Si no matchea → 404.
        if (!IsAllowedGooglePhotoHost(photoUri))
            return NotFound();

        // 302 Location: {photoUri}. La app sigue el redirect y baja los bytes del CDN de
        // Google directamente (no tocan Railway → sin egress). No se streamean bytes aquí.
        return Redirect(photoUri);
    }

    /// <summary>
    /// True solo si <paramref name="photoUri"/> es una URL https absoluta cuyo host es
    /// exactamente <c>googleusercontent.com</c> o un subdominio (<c>*.googleusercontent.com</c>).
    /// Compara el <see cref="Uri.Host"/> parseado, no substrings: un host como
    /// <c>googleusercontent.com.attacker.example</c> NO termina en <c>.googleusercontent.com</c>
    /// y por tanto se rechaza (no evade la allowlist).
    /// </summary>
    private static bool IsAllowedGooglePhotoHost(string photoUri)
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
