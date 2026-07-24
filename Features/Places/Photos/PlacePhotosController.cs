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

        // El photoUri es efímero: prohibimos que cualquier intermediario cachee el 302, o un
        // cliente seguiría un redirect caducado. Coherente con la regla ToS de no-caché.
        Response.Headers.CacheControl = "no-store";

        // 302 Location: {photoUri}. La app sigue el redirect y baja los bytes del CDN de
        // Google directamente (no tocan Railway → sin egress). No se streamean bytes aquí.
        return Redirect(photoUri);
    }
}
