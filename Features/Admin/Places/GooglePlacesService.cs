using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalList.API.NET.Features.Admin.Places;

public interface IGooglePlacesService
{
    /// <summary>
    /// Returns null when the API key is not configured — callers should return 404/503.
    /// Returns empty list when the query yields no results.
    /// </summary>
    Task<List<GooglePlacePreview>?> SearchAsync(string textQuery, CancellationToken ct);
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

    private const string FieldMask =
        "places.id,places.displayName,places.formattedAddress," +
        "places.location,places.types,places.rating,places.userRatingCount," +
        "places.priceLevel,places.photos,places.websiteUri," +
        "places.internationalPhoneNumber";

    public GooglePlacesService(HttpClient http, IConfiguration config, ILogger<GooglePlacesService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<List<GooglePlacePreview>?> SearchAsync(string textQuery, CancellationToken ct)
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
        request.Headers.Add("X-Goog-FieldMask", FieldMask);
        request.Content = JsonContent.Create(new
        {
            textQuery,
            maxResultCount = 20,
            languageCode = "en"
        });

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
                    Photos: ResolvePhotos(p.Photos, apiKey),
                    Types: p.Types ?? [],
                    Website: p.WebsiteUri,
                    Phone: p.InternationalPhoneNumber,
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

    private static string? MapPriceLevel(string? level) => level switch
    {
        "PRICE_LEVEL_FREE" or "PRICE_LEVEL_INEXPENSIVE" => "$",
        "PRICE_LEVEL_MODERATE" => "$$",
        "PRICE_LEVEL_EXPENSIVE" => "$$$",
        "PRICE_LEVEL_VERY_EXPENSIVE" => "$$$$",
        _ => null
    };

    private static List<string> ResolvePhotos(List<GooglePhotoRef>? photos, string apiKey) =>
        photos?
            .Where(p => !string.IsNullOrEmpty(p.Name))
            .Take(3)
            .Select(p => $"https://places.googleapis.com/v1/{p.Name}/media?maxWidthPx=1600&key={apiKey}")
            .ToList() ?? [];

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
        public decimal? Rating { get; set; }
        public int? UserRatingCount { get; set; }
        public string? PriceLevel { get; set; }
        public List<GooglePhotoRef>? Photos { get; set; }
        public string? WebsiteUri { get; set; }
        public string? InternationalPhoneNumber { get; set; }
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
}
