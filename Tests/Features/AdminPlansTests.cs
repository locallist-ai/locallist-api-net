using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

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
