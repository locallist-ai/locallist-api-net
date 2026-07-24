using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LocalList.API.NET.Shared.Dtos;

namespace LocalList.API.NET.Features.Admin.Places;

public interface IGooglePlacesService
{
    /// <summary>
    /// Returns null when the API key is not configured — callers should return 404/503.
    /// Returns empty list when the query yields no results.
    /// </summary>
    Task<List<GooglePlacePreview>?> SearchAsync(string textQuery, CancellationToken ct, decimal? lat = null, decimal? lng = null);

    /// <summary>
    /// Fetches full place details by Place ID. Returns null on API error or missing key.
    /// </summary>
    Task<GooglePlaceDetails?> GetDetailsAsync(string placeId, CancellationToken ct);

    /// <summary>
    /// Resolves a Google Maps URL or short link to a Place ID.
    /// Accepts: Place IDs directly, full Maps URLs, maps.app.goo.gl short URLs.
    /// Returns null when the URL cannot be resolved.
    /// </summary>
    Task<string?> ResolvePlaceIdFromUrlAsync(string input, CancellationToken ct);
}

public class GooglePlacesService : IGooglePlacesService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<GooglePlacesService> _logger;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string SearchFieldMask =
        "places.id,places.displayName,places.formattedAddress," +
        "places.location,places.types,places.rating,places.userRatingCount," +
        "places.priceLevel,places.photos,places.websiteUri," +
        "places.internationalPhoneNumber,places.editorialSummary";

    private const string DetailsFieldMask =
        "id,displayName,formattedAddress,location,types,primaryType," +
        "photos,priceLevel,rating,userRatingCount,websiteUri," +
        "internationalPhoneNumber,editorialSummary,addressComponents," +
        "regularOpeningHours";

    // Matches Place IDs: ChIJ... (most common) or other formats starting with upper+lower letters
    private static readonly Regex PlaceIdRegex =
        new(@"^[A-Za-z]{3}[A-Za-z0-9_\-]{20,}$", RegexOptions.Compiled);

    // Extracts PlaceId from the !1s...! segment of a long Google Maps URL
    private static readonly Regex PlaceIdInPathRegex =
        new(@"[!&?]1s(ChI[A-Za-z0-9_\-]+)", RegexOptions.Compiled);

    // Extracts place_id= query param
    private static readonly Regex PlaceIdQueryRegex =
        new(@"[?&]place_id=([A-Za-z0-9_\-]+)", RegexOptions.Compiled);

    public GooglePlacesService(HttpClient http, IConfiguration config, ILogger<GooglePlacesService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<List<GooglePlacePreview>?> SearchAsync(string textQuery, CancellationToken ct, decimal? lat = null, decimal? lng = null)
    {
        var apiKey = _config["GooglePlaces:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("GooglePlaces:ApiKey not configured — search disabled");
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://places.googleapis.com/v1/places:searchText");
        request.Headers.Add("X-Goog-Api-Key", apiKey);
        request.Headers.Add("X-Goog-FieldMask", SearchFieldMask);

        object body = (lat.HasValue && lng.HasValue)
            ? new
            {
                textQuery,
                maxResultCount = 20,
                languageCode = "en",
                locationBias = new
                {
                    circle = new
                    {
                        center = new { latitude = lat.Value, longitude = lng.Value },
                        radius = 5000.0
                    }
                }
            }
            : (object)new { textQuery, maxResultCount = 20, languageCode = "en" };

        request.Content = JsonContent.Create(body);

        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google Places API returned {Status} for query '{Query}'",
                    response.StatusCode, textQuery);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var result = await JsonSerializer.DeserializeAsync<GoogleApiResponse>(stream, _json, ct);

            if (result?.Places is null) return [];

            return result.Places
                .Select(p => new GooglePlacePreview(
                    GooglePlaceId: p.Id ?? string.Empty,
                    Name: p.DisplayName?.Text ?? p.Id ?? "Unknown",
                    FormattedAddress: p.FormattedAddress,
                    Lat: p.Location?.Latitude,
                    Lng: p.Location?.Longitude,
                    Rating: p.Rating,
                    ReviewCount: p.UserRatingCount,
                    PriceLevel: MapPriceLevel(p.PriceLevel),
                    Photos: ResolvePhotos(p.Photos, p.Id ?? string.Empty),
                    Types: p.Types ?? [],
                    Website: p.WebsiteUri,
                    Phone: p.InternationalPhoneNumber,
                    EditorialSummary: p.EditorialSummary?.Text,
                    ExistsInLib: false
                ))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Google Places API request failed for '{Query}'", textQuery);
            return null;
        }
    }

    public async Task<GooglePlaceDetails?> GetDetailsAsync(string placeId, CancellationToken ct)
    {
        var apiKey = _config["GooglePlaces:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("GooglePlaces:ApiKey not configured — GetDetails disabled");
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://places.googleapis.com/v1/places/{Uri.EscapeDataString(placeId)}");
        request.Headers.Add("X-Goog-Api-Key", apiKey);
        request.Headers.Add("X-Goog-FieldMask", DetailsFieldMask);

        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google Places Details API returned {Status} for placeId '{PlaceId}'",
                    response.StatusCode, placeId);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var p = await JsonSerializer.DeserializeAsync<GooglePlaceResult>(stream, _json, ct);
            if (p is null) return null;

            var city = p.AddressComponents?
                .FirstOrDefault(c => c.Types?.Contains("locality") == true)?.LongText;
            var neighborhood = p.AddressComponents?
                .FirstOrDefault(c => c.Types?.Contains("sublocality_level_1") == true)?.LongText;

            OpeningHoursData? openingHours = null;
            if (p.RegularOpeningHours?.Periods is { Count: > 0 } gPeriods)
            {
                openingHours = new OpeningHoursData(
                    Periods: gPeriods
                        .Select(gp => new OpeningPeriod(
                            Open:  gp.Open  is null ? null : new OpeningTime(gp.Open.Day,  gp.Open.Hour,  gp.Open.Minute),
                            Close: gp.Close is null ? null : new OpeningTime(gp.Close.Day, gp.Close.Hour, gp.Close.Minute)))
                        .ToList(),
                    WeekdayDescriptions: p.RegularOpeningHours.WeekdayDescriptions ?? []);
            }

            return new GooglePlaceDetails(
                Id: p.Id ?? placeId,
                Name: p.DisplayName?.Text ?? placeId,
                FormattedAddress: p.FormattedAddress,
                City: city,
                Neighborhood: neighborhood,
                Lat: p.Location?.Latitude,
                Lng: p.Location?.Longitude,
                PrimaryType: p.PrimaryType,
                Types: p.Types ?? [],
                PriceLevel: MapPriceLevel(p.PriceLevel),
                Photos: ResolvePhotos(p.Photos, p.Id ?? placeId),
                Rating: p.Rating,
                ReviewCount: p.UserRatingCount,
                Website: p.WebsiteUri,
                Phone: p.InternationalPhoneNumber,
                EditorialSummary: p.EditorialSummary?.Text,
                OpeningHours: openingHours
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Google Places Details API request failed for '{PlaceId}'", placeId);
            return null;
        }
    }

    public async Task<string?> ResolvePlaceIdFromUrlAsync(string input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        input = input.Trim();

        // 1. Already a Place ID?
        if (PlaceIdRegex.IsMatch(input) && !input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return input;

        // 2. place_id= query param?
        var qmatch = PlaceIdQueryRegex.Match(input);
        if (qmatch.Success) return qmatch.Groups[1].Value;

        // 3. ChIJ... embedded in URL path (long Maps URLs)
        var pmatch = PlaceIdInPathRegex.Match(input);
        if (pmatch.Success) return pmatch.Groups[1].Value;

        // 4. Short URL (maps.app.goo.gl, goo.gl/maps, g.co) — follow redirects manually
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
            (uri.Host.EndsWith("goo.gl", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith("g.co", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith("maps.app.goo.gl", StringComparison.OrdinalIgnoreCase)))
        {
            var expanded = await FollowRedirectAsync(input, ct);
            if (expanded != null && expanded != input)
            {
                // Try to extract PlaceId from the expanded URL
                var expandedMatch = PlaceIdInPathRegex.Match(expanded);
                if (expandedMatch.Success) return expandedMatch.Groups[1].Value;

                var expandedQmatch = PlaceIdQueryRegex.Match(expanded);
                if (expandedQmatch.Success) return expandedQmatch.Groups[1].Value;

                // Fallback: extract place name from URL and search
                return await SearchByNameFromMapsUrl(expanded, ct);
            }
        }

        // 5. Full Maps URL without embedded ChI — try name extraction + search
        if (input.Contains("google.com/maps", StringComparison.OrdinalIgnoreCase))
            return await SearchByNameFromMapsUrl(input, ct);

        return null;
    }

    // Follows up to 5 HTTP redirects without AllowAutoRedirect, returns the final Location.
    private async Task<string?> FollowRedirectAsync(string url, CancellationToken ct)
    {
        var current = url;
        for (var i = 0; i < 5; i++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, current);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; LocalList/1.0)");
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                if (resp.StatusCode is System.Net.HttpStatusCode.MovedPermanently
                    or System.Net.HttpStatusCode.Found
                    or System.Net.HttpStatusCode.SeeOther
                    or System.Net.HttpStatusCode.TemporaryRedirect
                    or System.Net.HttpStatusCode.PermanentRedirect)
                {
                    var location = resp.Headers.Location?.ToString();
                    if (string.IsNullOrEmpty(location)) break;
                    current = location.StartsWith("http") ? location : new Uri(new Uri(current), location).ToString();
                    continue;
                }

                return resp.IsSuccessStatusCode ? current : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Redirect follow failed for '{Url}'", current);
                return null;
            }
        }
        return current;
    }

    // Extracts place name (and optionally coordinates) from a long Maps URL and calls SearchAsync.
    private async Task<string?> SearchByNameFromMapsUrl(string url, CancellationToken ct)
    {
        var nameMatch = Regex.Match(url, @"/maps/place/([^/@?]+)", RegexOptions.IgnoreCase);
        if (!nameMatch.Success) return null;

        var rawName = Uri.UnescapeDataString(nameMatch.Groups[1].Value)
            .Replace('+', ' ')
            .Replace("_", " ");

        if (string.IsNullOrWhiteSpace(rawName)) return null;

        // Extract coordinates for location bias so the text search is anchored to the right city.
        decimal? lat = null, lng = null;
        var coordMatch = Regex.Match(url, @"/@([-\d.]+),([-\d.]+)", RegexOptions.IgnoreCase);
        if (coordMatch.Success
            && decimal.TryParse(coordMatch.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedLat)
            && decimal.TryParse(coordMatch.Groups[2].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedLng))
        {
            lat = parsedLat;
            lng = parsedLng;
        }

        var results = await SearchAsync(rawName, ct, lat, lng);
        return results?.FirstOrDefault()?.GooglePlaceId;
    }

    public static string? MapPriceLevel(string? level) => level switch
    {
        "PRICE_LEVEL_FREE" => "FREE",
        "PRICE_LEVEL_INEXPENSIVE" => "$",
        "PRICE_LEVEL_MODERATE" => "$$",
        "PRICE_LEVEL_EXPENSIVE" => "$$$",
        "PRICE_LEVEL_VERY_EXPENSIVE" => "$$$$",
        _ => null
    };

    /// <summary>
    /// NUNCA construye una URL <c>places.googleapis.com</c> con la key en el query string
    /// (ese leak es justo lo que T1+T3 cierran). Devuelve en su lugar hasta 3 referencias al
    /// preview admin-authed (<see cref="AdminPlacePhotoPreviewUrls"/>), que resuelve la key
    /// server-side vía <c>IPlacePhotoService</c> y responde con un 302. Único punto de síntesis
    /// de fotos de este servicio: no dupliques esta lógica en otro caller.
    /// </summary>
    private List<string> ResolvePhotos(List<GooglePhotoRef>? photos, string googlePlaceId)
    {
        var count = photos?.Count(p => !string.IsNullOrEmpty(p.Name)) ?? 0;
        if (count == 0) return [];

        return AdminPlacePhotoPreviewUrls.ForGooglePlace(
            googlePlaceId, Math.Min(count, 3), _config["Api:PublicBaseUrl"]);
    }

    // --- private response models ---
    private sealed class GoogleApiResponse
    {
        public List<GooglePlaceResult>? Places { get; set; }
    }

    private sealed class GooglePlaceResult
    {
        public string? Id { get; set; }
        public GoogleLocalizedText? DisplayName { get; set; }
        public string? FormattedAddress { get; set; }
        public GoogleLatLng? Location { get; set; }
        public List<string>? Types { get; set; }
        public string? PrimaryType { get; set; }
        public decimal? Rating { get; set; }
        public int? UserRatingCount { get; set; }
        public string? PriceLevel { get; set; }
        public List<GooglePhotoRef>? Photos { get; set; }
        public string? WebsiteUri { get; set; }
        public string? InternationalPhoneNumber { get; set; }
        public GoogleLocalizedText? EditorialSummary { get; set; }
        public List<GoogleAddressComponent>? AddressComponents { get; set; }
        public GoogleOpeningHours? RegularOpeningHours { get; set; }
    }

    private sealed class GoogleOpeningHours
    {
        public List<GooglePeriod>? Periods { get; set; }
        public List<string>? WeekdayDescriptions { get; set; }
    }

    private sealed class GooglePeriod
    {
        public GoogleTimeOfDay? Open { get; set; }
        public GoogleTimeOfDay? Close { get; set; }
    }

    private sealed class GoogleTimeOfDay
    {
        public int Day { get; set; }
        public int Hour { get; set; }
        public int Minute { get; set; }
    }

    private sealed class GoogleLocalizedText
    {
        public string Text { get; set; } = string.Empty;
    }

    private sealed class GoogleLatLng
    {
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
    }

    private sealed class GooglePhotoRef
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class GoogleAddressComponent
    {
        public string? LongText { get; set; }
        public string? ShortText { get; set; }
        public List<string>? Types { get; set; }
    }
}

public record GooglePlaceDetails(
    string Id,
    string Name,
    string? FormattedAddress,
    string? City,
    string? Neighborhood,
    decimal? Lat,
    decimal? Lng,
    string? PrimaryType,
    List<string> Types,
    string? PriceLevel,
    List<string> Photos,
    decimal? Rating,
    int? ReviewCount,
    string? Website,
    string? Phone,
    string? EditorialSummary,
    OpeningHoursData? OpeningHours = null
);
