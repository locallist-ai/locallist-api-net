using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalList.API.NET.Features.Places.Photos;

public interface IPlacePhotoService
{
    /// <summary>
    /// Resuelve al vuelo (runtime-only, sin almacenar nada — requisito ToS) la URL efímera
    /// del CDN de Google (<c>lh3.googleusercontent.com</c>) para la foto <paramref name="index"/>
    /// del sitio de Google <paramref name="googlePlaceId"/>. Devuelve <c>null</c> ante
    /// CUALQUIER condición de degradación (sin key, place sin fotos, index fuera de rango,
    /// presupuesto agotado, o error/timeout de Google) para que el endpoint responda 404 y la
    /// app degrade a gradiente — nunca 500. La API key vive solo en el header server-side y
    /// jamás forma parte del valor devuelto.
    /// </summary>
    Task<string?> ResolvePhotoUriAsync(string googlePlaceId, int index, CancellationToken ct);
}

public sealed class PlacePhotoService : IPlacePhotoService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly PhotoBudgetCounter _budget;
    private readonly ILogger<PlacePhotoService> _logger;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Place Details con FieldMask SOLO `photos` → SKU gratis ("Place Details Essentials
    // IDs-Only"). NO añadir campos de pago aquí: encarecería cada request de foto.
    // Verificado contra la doc primaria de Google (2026-07-24): en la tabla data-fields de
    // Place Details, `photos` pertenece al tier "IDs Only" (junto a `id`/`name`/`attributions`),
    // que NO factura. El único SKU de pago del proxy es `/media` ($0.007/foto).
    private const string PhotoFieldMask = "photos";

    // Ancho servido para la hero. El coste de /media es plano por llamada (no por tamaño),
    // así que pedimos una resolución generosa apta para pantallas retina.
    private const int MaxWidthPx = 1600;

    public PlacePhotoService(
        HttpClient http,
        IConfiguration config,
        PhotoBudgetCounter budget,
        ILogger<PlacePhotoService> logger)
    {
        _http = http;
        _config = config;
        _budget = budget;
        _logger = logger;
    }

    public async Task<string?> ResolvePhotoUriAsync(string googlePlaceId, int index, CancellationToken ct)
    {
        // Key separada con fallback a la de ingesta admin. Sin NINGUNA de las dos → degradar.
        var apiKey = _config["GooglePlaces:PhotoApiKey"] ?? _config["GooglePlaces:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning(
                "GooglePlaces photo key not configured (PhotoApiKey/ApiKey) — photo proxy disabled");
            return null;
        }

        if (index < 0)
            return null;

        // 0. Peek NO consumidor del presupuesto: si el cap del día ya está agotado, cortamos
        //    AQUÍ (404) sin siquiera emitir la llamada gratis de Place Details, que se
        //    desperdiciaría porque el /media de pago que vendría después no se autorizaría. NO
        //    consume slot: el conteo real (= coste) lo sigue haciendo el TryAcquire de abajo,
        //    justo antes del /media, así el contador cuenta solo llamadas /media reales.
        if (_budget.IsExhausted)
        {
            _logger.LogWarning("Photo daily budget cap reached: degrading photo request to 404 (pre-Details)");
            return null;
        }

        // 1. Place Details (FieldMask=photos, GRATIS): resuelve el photo name fresco. El name
        //    CADUCA y no se puede cachear (ToS) → siempre al vuelo.
        var photoName = await ResolvePhotoNameAsync(googlePlaceId, index, apiKey, ct);
        if (string.IsNullOrEmpty(photoName))
            return null;

        // 2. Circuit breaker de presupuesto: reservamos ANTES de la llamada de pago. Si el cap
        //    del día ya se alcanzó, NO se emite la llamada /media (degradar a 404).
        if (!_budget.TryAcquire())
        {
            _logger.LogWarning("Photo daily budget cap reached — degrading photo request to 404");
            return null;
        }

        // 3. /media con skipHttpRedirect=true + key en HEADER (nunca en query): Google devuelve
        //    JSON con photoUri (URL efímera del CDN). Respondemos 302 a esa URL sin streamear.
        return await ResolvePhotoUriFromNameAsync(photoName, apiKey, ct);
    }

    private async Task<string?> ResolvePhotoNameAsync(
        string googlePlaceId, int index, string apiKey, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://places.googleapis.com/v1/places/{Uri.EscapeDataString(googlePlaceId)}");
            request.Headers.Add("X-Goog-Api-Key", apiKey);
            request.Headers.Add("X-Goog-FieldMask", PhotoFieldMask);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Google Place Details (photos) returned {Status} for placeId '{PlaceId}'",
                    response.StatusCode, googlePlaceId);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var details = await JsonSerializer.DeserializeAsync<PhotoDetailsResponse>(stream, _json, ct);

            var photos = details?.Photos;
            if (photos is null || index >= photos.Count)
                return null;

            var name = photos[index].Name;
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Google Place Details (photos) failed for '{PlaceId}'", googlePlaceId);
            return null;
        }
    }

    private async Task<string?> ResolvePhotoUriFromNameAsync(string photoName, string apiKey, CancellationToken ct)
    {
        try
        {
            // Key en HEADER, NO en query (query se loguearía). skipHttpRedirect=true → JSON
            // con photoUri en vez de un 302 con bytes.
            var url = $"https://places.googleapis.com/v1/{photoName}/media" +
                      $"?maxWidthPx={MaxWidthPx}&skipHttpRedirect=true";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Goog-Api-Key", apiKey);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google photo media returned {Status}", response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var media = await JsonSerializer.DeserializeAsync<PhotoMediaResponse>(stream, _json, ct);

            return string.IsNullOrEmpty(media?.PhotoUri) ? null : media.PhotoUri;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Google photo media request failed");
            return null;
        }
    }

    // --- response models ---
    private sealed class PhotoDetailsResponse
    {
        public List<PhotoRef>? Photos { get; set; }
    }

    private sealed class PhotoRef
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class PhotoMediaResponse
    {
        [JsonPropertyName("photoUri")]
        public string? PhotoUri { get; set; }
    }
}
