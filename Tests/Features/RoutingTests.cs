using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

public class RoutingTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<(Guid PlanId, Guid PlaceAId, Guid PlaceBId)> SeedPlanWithCoords(
        int dayCount = 1, int stopsPerDay = 2, string suffix = "")
    {
        var db = fixture.GetDbContext();
        var planId = Guid.NewGuid();
        db.Plans.Add(new Plan { Id = planId, Name = $"Route Test{suffix}", City = "Madrid", Type = "custom" });

        var placeAId = Guid.NewGuid();
        var placeBId = Guid.NewGuid();
        db.Places.Add(new Place
        {
            Id = placeAId, Name = $"PlaceA{suffix}", Category = "Food", WhyThisPlace = "test",
            Status = "published", Latitude = 40.4168m, Longitude = -3.7038m
        });
        db.Places.Add(new Place
        {
            Id = placeBId, Name = $"PlaceB{suffix}", Category = "Art", WhyThisPlace = "test",
            Status = "published", Latitude = 40.4200m, Longitude = -3.7000m
        });

        for (int day = 1; day <= dayCount; day++)
        {
            if (stopsPerDay >= 1)
                db.PlanStops.Add(new PlanStop { Id = Guid.NewGuid(), PlanId = planId, PlaceId = placeAId, DayNumber = day, OrderIndex = 0 });
            if (stopsPerDay >= 2)
                db.PlanStops.Add(new PlanStop { Id = Guid.NewGuid(), PlanId = planId, PlaceId = placeBId, DayNumber = day, OrderIndex = 1 });
        }

        await db.SaveChangesAsync();
        return (planId, placeAId, placeBId);
    }

    [Fact]
    public async Task GetPlan_CacheMiss_PopulatesRouteSegments()
    {
        fixture.FakeMapbox.Calls.Clear();
        fixture.FakeMapbox.Responder = null;

        var (planId, _, _) = await SeedPlanWithCoords(suffix: "-miss");
        var client = fixture.CreateClient();

        var response = await client.GetAsync($"/plans/{planId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var segments = body.GetProperty("routeSegments");
        Assert.Equal(JsonValueKind.Array, segments.ValueKind);
        Assert.True(segments.GetArrayLength() > 0, "routeSegments should be non-empty after cache miss");

        var seg = segments[0];
        Assert.Equal(1, seg.GetProperty("dayNumber").GetInt32());
        Assert.Equal(0, seg.GetProperty("fromOrderIndex").GetInt32());
        Assert.Equal(1, seg.GetProperty("toOrderIndex").GetInt32());
        Assert.False(string.IsNullOrEmpty(seg.GetProperty("encodedPolyline").GetString()), "polyline should be set");

        Assert.True(fixture.FakeMapbox.Calls.Count > 0, "Mapbox should have been called on cache miss");

        // Verify DB row was persisted
        var db = fixture.GetDbContext();
        var rows = db.RouteSegmentCaches.ToList();
        Assert.True(rows.Count > 0, "route_segment_cache should have at least one row");
    }

    [Fact]
    public async Task GetPlan_CacheHit_DoesNotCallMapbox()
    {
        fixture.FakeMapbox.Calls.Clear();
        fixture.FakeMapbox.Responder = _ => throw new InvalidOperationException("Mapbox should not be called on cache hit");

        var (planId, placeAId, placeBId) = await SeedPlanWithCoords(suffix: "-hit");

        // Pre-seed cache
        var db = fixture.GetDbContext();
        db.RouteSegmentCaches.Add(new RouteSegmentCache
        {
            Id = Guid.NewGuid(),
            FromPlaceId = placeAId,
            ToPlaceId = placeBId,
            Mode = "walking",
            EncodedPolyline = "cached_polyline",
            DistanceMeters = 400,
            DurationSeconds = 320,
            ComputedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/plans/{planId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var segments = body.GetProperty("routeSegments");
        Assert.Equal(JsonValueKind.Array, segments.ValueKind);
        Assert.Equal("cached_polyline", segments[0].GetProperty("encodedPolyline").GetString());

        Assert.Empty(fixture.FakeMapbox.Calls);
    }

    [Fact]
    public async Task GetPlan_MapboxFailure_Returns200WithEmptySegments()
    {
        fixture.FakeMapbox.Calls.Clear();
        fixture.FakeMapbox.Responder = _ => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway);

        var (planId, _, _) = await SeedPlanWithCoords(suffix: "-502");
        var client = fixture.CreateClient();

        var response = await client.GetAsync($"/plans/{planId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Plan body should be intact
        Assert.True(body.TryGetProperty("name", out var name) && name.GetString() is not null);
        // routeSegments should be null (serialized as absent) or empty
        var hasSegments = body.TryGetProperty("routeSegments", out var segments);
        Assert.True(!hasSegments || segments.ValueKind == JsonValueKind.Null || segments.GetArrayLength() == 0,
            "routeSegments should be absent or empty when Mapbox fails");
    }

    [Fact]
    public async Task GetPlan_SingleStopDay_HasNoSegments()
    {
        fixture.FakeMapbox.Calls.Clear();
        fixture.FakeMapbox.Responder = null;

        var (planId, _, _) = await SeedPlanWithCoords(stopsPerDay: 1, suffix: "-single");
        var client = fixture.CreateClient();

        var response = await client.GetAsync($"/plans/{planId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var hasSegments = body.TryGetProperty("routeSegments", out var segments);
        Assert.True(!hasSegments || segments.ValueKind == JsonValueKind.Null || segments.GetArrayLength() == 0,
            "Single-stop day should produce no route segments");
        Assert.Empty(fixture.FakeMapbox.Calls);
    }

    [Fact]
    public async Task GetPlan_I18nPreservedWithRouteSegments()
    {
        fixture.FakeMapbox.Calls.Clear();
        fixture.FakeMapbox.Responder = null;

        var db = fixture.GetDbContext();
        var planId = Guid.NewGuid();
        var placeAId = Guid.NewGuid();
        var placeBId = Guid.NewGuid();

        var nameI18n = JsonDocument.Parse("{\"en\":\"Test Plan\",\"es\":\"Plan de Prueba\"}");
        db.Plans.Add(new Plan
        {
            Id = planId, Name = "Test Plan", City = "Madrid", Type = "custom",
            Source = "user", NameI18n = nameI18n
        });
        db.Places.Add(new Place { Id = placeAId, Name = "A", Category = "Food", WhyThisPlace = "x", Status = "published", Latitude = 40.4168m, Longitude = -3.7038m });
        db.Places.Add(new Place { Id = placeBId, Name = "B", Category = "Art", WhyThisPlace = "y", Status = "published", Latitude = 40.4200m, Longitude = -3.7000m });
        db.PlanStops.Add(new PlanStop { Id = Guid.NewGuid(), PlanId = planId, PlaceId = placeAId, DayNumber = 1, OrderIndex = 0 });
        db.PlanStops.Add(new PlanStop { Id = Guid.NewGuid(), PlanId = planId, PlaceId = placeBId, DayNumber = 1, OrderIndex = 1 });
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("es");

        var response = await client.GetAsync($"/plans/{planId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Plan de Prueba", body.GetProperty("name").GetString());
        var segments = body.GetProperty("routeSegments");
        Assert.Equal(JsonValueKind.Array, segments.ValueKind);
    }

    [Fact]
    public async Task GetPlan_ConcurrentRequests_RouteSegmentCacheIsIdempotent()
    {
        fixture.FakeMapbox.Calls.Clear();
        fixture.FakeMapbox.Responder = null;

        var (planId, placeAId, placeBId) = await SeedPlanWithCoords(suffix: "-concurrent");

        // Fire two parallel requests against a cold cache
        var client1 = fixture.CreateClient();
        var client2 = fixture.CreateClient();
        var task1 = client1.GetAsync($"/plans/{planId}");
        var task2 = client2.GetAsync($"/plans/{planId}");

        var responses = await Task.WhenAll(task1, task2);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        // The unique constraint + ON CONFLICT DO NOTHING ensures exactly 1 cache row per pair
        var db = fixture.GetDbContext();
        var rowCount = db.RouteSegmentCaches
            .Count(r => r.FromPlaceId == placeAId && r.ToPlaceId == placeBId && r.Mode == "walking");
        Assert.Equal(1, rowCount);
    }
}
