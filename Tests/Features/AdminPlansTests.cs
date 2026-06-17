using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Tests de shape para AdminPlansController. Verifican que las respuestas
/// emiten los campos de los DTOs admin (no entidades EF raw) y que el campo
/// <c>id</c>/<c>name</c> viene a nivel raíz en CreatePlan (contrato que
/// <c>locallist-admin</c> consume al hacer <c>res.data.id</c> tras POST).
/// </summary>
public class AdminPlansTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task GetPlans_ResponseShape_MatchesAdminPlanDto()
    {
        var db = fixture.GetDbContext();
        db.Plans.Add(new Plan
        {
            Id = Guid.NewGuid(),
            Name = $"Admin Shape Plan {Guid.NewGuid():N}",
            City = "Miami",
            Type = "curated",
            Source = "curated"
        });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var response = await client.GetAsync("/admin/plans");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("plans", out var plans));
        Assert.True(body.TryGetProperty("total", out _));

        foreach (var plan in plans.EnumerateArray())
        {
            Assert.True(plan.TryGetProperty("id", out _));
            Assert.True(plan.TryGetProperty("name", out _));
            Assert.True(plan.TryGetProperty("source", out _)); // campo admin-only
            // EF navigations no deben aparecer.
            Assert.False(plan.TryGetProperty("stops", out _));
            Assert.False(plan.TryGetProperty("createdBy", out _));
            Assert.False(plan.TryGetProperty("followSessions", out _));
        }
    }

    [Fact]
    public async Task GetPlan_ResponseShape_IncludesDaysWithAdminPlaceDto()
    {
        var db = fixture.GetDbContext();
        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = $"Admin Shape Place {Guid.NewGuid():N}",
            Category = "Food",
            City = "Miami",
            WhyThisPlace = "admin shape",
            Status = "published",
            RejectionReason = "admin-visible",
            AiVibeScore = 77
        };
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = $"Admin Detail Shape {Guid.NewGuid():N}",
            City = "Miami",
            Type = "curated",
            Source = "curated",
            DurationDays = 1
        };
        db.Places.Add(place);
        db.Plans.Add(plan);
        db.PlanStops.Add(new PlanStop
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            PlaceId = place.Id,
            DayNumber = 1,
            OrderIndex = 0
        });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var response = await client.GetAsync($"/admin/plans/{plan.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("source", out _));
        var stop = body.GetProperty("days").EnumerateArray().First()
            .GetProperty("stops").EnumerateArray().First();
        var stopPlace = stop.GetProperty("place");
        // En admin SÍ queremos ver los campos de curación.
        Assert.True(stopPlace.TryGetProperty("rejectionReason", out _));
        Assert.True(stopPlace.TryGetProperty("aiVibeScore", out _));
    }

    [Fact]
    public async Task CreatePlan_ResponseShape_HasIdAndNameAtRoot()
    {
        var db = fixture.GetDbContext();
        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = $"Seed {Guid.NewGuid():N}",
            Category = "Food",
            City = "Miami",
            WhyThisPlace = "admin create shape",
            Status = "published"
        };
        db.Places.Add(place);
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var payload = new
        {
            name = $"Created Plan {Guid.NewGuid():N}",
            city = "Miami",
            type = "curated",
            durationDays = 1,
            isPublic = true,
            stops = new[]
            {
                new { placeId = place.Id, dayNumber = 1, orderIndex = 0 }
            }
        };

        var response = await client.PostAsJsonAsync("/admin/plans", payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Contrato que el admin-web consume: res.data.id y res.data.name al nivel raíz.
        Assert.True(body.TryGetProperty("id", out _));
        Assert.True(body.TryGetProperty("name", out var name));
        Assert.Equal(payload.name, name.GetString());
        Assert.True(body.TryGetProperty("days", out _));
        // No debe haber un wrapper { plan: {...}, stops: N } (contrato anterior roto).
        Assert.False(body.TryGetProperty("plan", out _));
    }

    [Fact]
    public async Task TranslateBatch_Plans_LimitSmallerThanPending_Returns_RemainingGreaterThanZero()
    {
        var db = fixture.GetDbContext();
        for (var i = 0; i < 3; i++)
        {
            db.Plans.Add(new Plan
            {
                Id = Guid.NewGuid(),
                Name = $"Translate Plan {Guid.NewGuid():N}",
                City = "Miami",
                Type = "curated",
                Source = "curated",
                Description = "Test plan",
            });
        }
        await db.SaveChangesAsync();

        fixture.FakeGemini.Responder = _ => GeminiOk("""{"name":"Nombre ES","description":"Descripción ES"}""");
        try
        {
            var client = CreateAdminClient();
            var response = await client.PostAsync("/admin/plans/translate-batch?lang=es&limit=1", content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, body.GetProperty("translated").GetInt32());
            Assert.True(body.GetProperty("remaining").GetInt32() > 0);
        }
        finally
        {
            fixture.FakeGemini.Responder = null;
        }
    }

    // ── Max 10 stops per day ────────────────────────────────────────────────

    [Fact]
    public async Task CreatePlan_With11StopsOnOneDay_Returns400()
    {
        var db = fixture.GetDbContext();
        var placeIds = new List<Guid>();
        for (var i = 0; i < 11; i++)
        {
            var p = new Place
            {
                Id = Guid.NewGuid(), Name = $"MaxStops Place {i} {Guid.NewGuid():N}",
                Category = "Food", City = "Miami", WhyThisPlace = "max test",
                Status = "published"
            };
            db.Places.Add(p);
            placeIds.Add(p.Id);
        }
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var stops = placeIds.Select((id, idx) => new { placeId = id, dayNumber = 1, orderIndex = idx }).ToArray();
        var payload = new { name = $"OverLimit {Guid.NewGuid():N}", city = "Miami", type = "curated", durationDays = 1, stops };

        var response = await client.PostAsJsonAsync("/admin/plans", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("too_many_stops_day_1", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UpdateStops_With11StopsOnOneDay_Returns400()
    {
        var db = fixture.GetDbContext();
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Name = $"UpdateStops Plan {Guid.NewGuid():N}",
            City = "Miami", Type = "curated", Source = "curated", DurationDays = 1
        };
        db.Plans.Add(plan);
        var placeIds = new List<Guid>();
        for (var i = 0; i < 11; i++)
        {
            var p = new Place
            {
                Id = Guid.NewGuid(), Name = $"UpdateMaxStops Place {i} {Guid.NewGuid():N}",
                Category = "Food", City = "Miami", WhyThisPlace = "update max test",
                Status = "published"
            };
            db.Places.Add(p);
            placeIds.Add(p.Id);
        }
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var stops = placeIds.Select((id, idx) => new { placeId = id, dayNumber = 1, orderIndex = idx }).ToArray();

        var response = await client.PutAsJsonAsync($"/admin/plans/{plan.Id}/stops", new { stops });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("too_many_stops_day_1", body.GetProperty("error").GetString());
    }

    // ── Atomic metadata + stops PATCH ───────────────────────────────────────

    [Fact]
    public async Task UpdatePlan_AtomicMetaAndStops_PersistsBoth()
    {
        var db = fixture.GetDbContext();
        var placeA = new Place
        {
            Id = Guid.NewGuid(), Name = $"Atomic A {Guid.NewGuid():N}",
            Category = "Food", City = "Miami", WhyThisPlace = "atomic", Status = "published"
        };
        var placeB = new Place
        {
            Id = Guid.NewGuid(), Name = $"Atomic B {Guid.NewGuid():N}",
            Category = "Food", City = "Miami", WhyThisPlace = "atomic", Status = "published"
        };
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Name = $"Atomic Plan {Guid.NewGuid():N}",
            City = "Miami", Type = "curated", Source = "curated", DurationDays = 1
        };
        db.Places.AddRange(placeA, placeB);
        db.Plans.Add(plan);
        db.PlanStops.Add(new PlanStop
        {
            Id = Guid.NewGuid(), PlanId = plan.Id, PlaceId = placeA.Id, DayNumber = 1, OrderIndex = 0
        });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var payload = new
        {
            name = "Atomic Renamed",
            stops = new[] { new { placeId = placeB.Id, dayNumber = 1, orderIndex = 0 } }
        };

        var response = await client.PatchAsJsonAsync($"/admin/plans/{plan.Id}", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Atomic Renamed", body.GetProperty("name").GetString());
        var stop = body.GetProperty("days").EnumerateArray().First()
            .GetProperty("stops").EnumerateArray().Single();
        Assert.Equal(placeB.Id, stop.GetProperty("placeId").GetGuid());

        // Both metadata and stops persisted (fresh context).
        var verify = fixture.GetDbContext();
        var saved = await verify.Plans.AsNoTracking()
            .Include(p => p.Stops)
            .FirstAsync(p => p.Id == plan.Id);
        Assert.Equal("Atomic Renamed", saved.Name);
        Assert.Single(saved.Stops);
        Assert.Equal(placeB.Id, saved.Stops.First().PlaceId);
    }

    [Fact]
    public async Task UpdatePlan_AtomicStopsWithBadPlace_RollsBackMetadataAndStops()
    {
        var db = fixture.GetDbContext();
        var place = new Place
        {
            Id = Guid.NewGuid(), Name = $"Rollback Place {Guid.NewGuid():N}",
            Category = "Food", City = "Miami", WhyThisPlace = "rollback", Status = "published"
        };
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Name = "Original Name",
            City = "Miami", Type = "curated", Source = "curated", DurationDays = 1
        };
        var originalStopId = Guid.NewGuid();
        db.Places.Add(place);
        db.Plans.Add(plan);
        db.PlanStops.Add(new PlanStop
        {
            Id = originalStopId, PlanId = plan.Id, PlaceId = place.Id, DayNumber = 1, OrderIndex = 0
        });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        // New metadata + a stop pointing at a non-existent place. The INSERT fails on the
        // place FK halfway through the transaction; the whole write must roll back.
        var payload = new
        {
            name = "Should Not Persist",
            stops = new[] { new { placeId = Guid.NewGuid(), dayNumber = 1, orderIndex = 0 } }
        };

        var response = await client.PatchAsJsonAsync($"/admin/plans/{plan.Id}", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Rollback: metadata unchanged AND the original stop is still the only stop.
        var verify = fixture.GetDbContext();
        var saved = await verify.Plans.AsNoTracking()
            .Include(p => p.Stops)
            .FirstAsync(p => p.Id == plan.Id);
        Assert.Equal("Original Name", saved.Name);
        Assert.Single(saved.Stops);
        Assert.Equal(originalStopId, saved.Stops.First().Id);
        Assert.Equal(place.Id, saved.Stops.First().PlaceId);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static HttpResponseMessage GeminiOk(string text)
    {
        var envelope = $"{{\"candidates\":[{{\"content\":{{\"parts\":[{{\"text\":{System.Text.Json.JsonSerializer.Serialize(text)}}}]}}}}]}}";
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private HttpClient CreateAdminClient()
    {
        var adminEmail = $"admin-plans-{Guid.NewGuid():N}@locallist.ai";
        var adminFbUid = $"fb-admin-plans-{Guid.NewGuid():N}";

        var db = fixture.GetDbContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Email = adminEmail,
            FirebaseUid = adminFbUid,
            Role = "admin"
        });
        db.SaveChanges();

        var client = fixture.CreateClient();
        var token = fixture.CreateToken(adminFbUid, adminEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
