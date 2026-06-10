using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace LocalList.API.NET.Features.Routing;

public class MapboxRoutingService : IRoutingService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<MapboxRoutingService> _logger;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public MapboxRoutingService(HttpClient http, IConfiguration config, ILogger<MapboxRoutingService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<RouteSegment?> GetRouteAsync(GeoPoint from, GeoPoint to, RoutingMode mode, CancellationToken ct)
    {
        var token = _config["Mapbox:AccessToken"];
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("Mapbox:AccessToken not configured — routing disabled");
            return null;
        }

        var profile = mode == RoutingMode.Walking ? "walking" : "driving";
        // Mapbox expects lng,lat order
        var coords = FormattableString.Invariant(
            $"{from.Lng},{from.Lat};{to.Lng},{to.Lat}");
        var url = $"https://api.mapbox.com/directions/v5/mapbox/{profile}/{coords}" +
                  $"?geometries=polyline6&overview=full&access_token={token}";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Mapbox Directions returned {StatusCode} for {Profile} route", response.StatusCode, profile);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var result = await JsonSerializer.DeserializeAsync<MapboxDirectionsResponse>(stream, _json, ct);

            if (result?.Code != "Ok" || result.Routes is not { Count: > 0 })
            {
                _logger.LogWarning("Mapbox Directions returned no routes (code={Code})", result?.Code);
                return null;
            }

            var route = result.Routes[0];
            var distance = (int)Math.Round(route.Distance);
            var duration = (int)Math.Round(route.Duration);
            return new RouteSegment(route.Geometry, distance, duration);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Mapbox Directions request failed");
            return null;
        }
    }

    private sealed class MapboxDirectionsResponse
    {
        public string Code { get; set; } = string.Empty;
        public List<MapboxRoute>? Routes { get; set; }
    }

    private sealed class MapboxRoute
    {
        public string Geometry { get; set; } = string.Empty;
        public double Distance { get; set; }
        public double Duration { get; set; }
    }
}
