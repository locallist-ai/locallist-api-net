using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Features.Places.Photos;

namespace LocalList.API.NET.Features.Admin.Places;

// Google Places search (for import candidates) + the admin-authed photo preview redirect.
// Logic is identical to the original single-file version; only its location changed.
public partial class AdminPlacesController
{
    [HttpPost("google-search")]
    public async Task<IActionResult> GoogleSearch([FromBody] GoogleSearchRequest request, CancellationToken ct)
    {
        var textQuery = $"{request.Query.Trim()} in {request.City.Trim()}";
        var previews = await _googlePlaces.SearchAsync(textQuery, ct);

        if (previews is null)
            return NotFound(new { error = "google_places_unavailable", message = "Google Places API key not configured or service unavailable." });

        if (previews.Count > 0)
        {
            var incomingIds = previews.Select(p => p.GooglePlaceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existing = (await _db.Places.AsNoTracking()
                .Where(p => p.GooglePlaceId != null && incomingIds.Contains(p.GooglePlaceId))
                .Select(p => p.GooglePlaceId!)
                .ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            previews = previews
                .Select(p => p with { ExistsInLib = existing.Contains(p.GooglePlaceId) })
                .ToList();
        }

        _logger.LogInformation("GoogleSearch: query='{Query}' returned {Count} results", textQuery, previews.Count);
        return Ok(new { results = previews });
    }

    /// <summary>
    /// Preview de una foto de Google DURANTE el import, antes de que el Place exista en DB (el
    /// curador aún no ha guardado el sitio, así que no hay un Place.Id interno con el que usar
    /// el proxy público de T1). Admin-authed (vía <c>[AdminAuthorize]</c> a nivel de clase) para
    /// que el navegador del admin nunca reciba la URL directa de Google con la key: reutiliza
    /// <see cref="IPlacePhotoService"/> de T1 (Place Details → <c>/media?skipHttpRedirect=true</c>
    /// con la key en el header → 302), sin duplicar esa lógica.
    /// </summary>
    [HttpGet("photo-preview")]
    public async Task<IActionResult> PhotoPreview(
        [FromQuery] string googlePlaceId, [FromQuery] int index, CancellationToken ct)
    {
        // no-store: el photoUri es efímero (ToS de Google), no debe cachearse en el navegador
        // del admin ni en ningún intermediario.
        Response.Headers.CacheControl = "no-store";

        if (string.IsNullOrWhiteSpace(googlePlaceId))
            return BadRequest(new { error = "googlePlaceId is required." });

        var photoUri = await _photos.ResolvePhotoUriAsync(googlePlaceId, index, ct);
        if (string.IsNullOrEmpty(photoUri))
            return NotFound();

        // Misma allowlist de host que el proxy público de T1: defensa en profundidad.
        if (!GooglePhotoHostValidator.IsAllowedGooglePhotoHost(photoUri))
            return NotFound();

        return Redirect(photoUri);
    }
}
