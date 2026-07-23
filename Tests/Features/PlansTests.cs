using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Usage;

namespace LocalList.API.Tests.Features;

public class PlansTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // ── F2: cupo de planes guardados enforced en POST /plans (decisión Pablo 2026-07-22) ──
    // Límite de ALMACENAMIENTO independiente del contador mensual de generación IA. Se movió
    // aquí desde PlanGenerationGateService para que un free con 5 planes manuales no reciba
    // 403 al GENERAR (que va contra el contador 3/mes, un límite distinto).

    [Fact]
    public async Task CreatePlan_FreeUser_UnderSavedLimit_Succeeds()
    {
        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(userId, $"plans-free-under-{userId:N}@test.com");
        await SeedSavedPlans(userId, PlanGenerationGateService.FreeSavedPlansLimit - 1); // 4 < 5

        var res = await client.PostAsJsonAsync("/plans", new { name = "Nuevo plan", city = "Miami", durationDays = 1 });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    public async Task CreatePlan_FreeUser_AtSavedLimit_Returns403Structured()
    {
        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(userId, $"plans-free-at-{userId:N}@test.com");
        await SeedSavedPlans(userId, PlanGenerationGateService.FreeSavedPlansLimit); // 5

        var res = await client.PostAsJsonAsync("/plans", new { name = "Uno más", city = "Miami", durationDays = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("saved_plans_limit_reached", body.GetProperty("error").GetString());
        Assert.Equal(PlanGenerationGateService.FreeSavedPlansLimit, body.GetProperty("used").GetInt32());
        Assert.Equal(PlanGenerationGateService.FreeSavedPlansLimit, body.GetProperty("limit").GetInt32());

        // No se persistió ningún plan extra.
        var db = fixture.GetDbContext();
        Assert.Equal(PlanGenerationGateService.FreeSavedPlansLimit,
            await db.Plans.CountAsync(p => p.CreatedById == userId));
    }

    [Fact]
    public async Task CreatePlan_FreeUser_AtLimit_DeleteFreesSlot_ThenSucceeds()
    {
        // m2/F5: DELETE /plans/:id libera hueco — probado de verdad (el test viejo sembraba y
        // NUNCA borraba). Free en el tope: POST 403; tras borrar uno, POST vuelve a pasar.
        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(userId, $"plans-free-del-{userId:N}@test.com");
        var seeded = await SeedSavedPlans(userId, PlanGenerationGateService.FreeSavedPlansLimit); // 5

        var blocked = await client.PostAsJsonAsync("/plans", new { name = "Bloqueado", city = "Miami", durationDays = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        var del = await client.DeleteAsync($"/plans/{seeded[0]}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var afterDelete = await client.PostAsJsonAsync("/plans", new { name = "Ahora sí", city = "Miami", durationDays = 1 });
        Assert.Equal(HttpStatusCode.Created, afterDelete.StatusCode);

        var db = fixture.GetDbContext();
        Assert.Equal(PlanGenerationGateService.FreeSavedPlansLimit,
            await db.Plans.CountAsync(p => p.CreatedById == userId)); // 5 - 1 + 1
    }

    [Fact]
    public async Task CreatePlan_ProUser_OverLimit_NotGated()
    {
        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            userId, $"plans-pro-{userId:N}@test.com", tier: "pro");
        await SeedSavedPlans(userId, PlanGenerationGateService.FreeSavedPlansLimit + 1); // 6 > 5

        var res = await client.PostAsJsonAsync("/plans", new { name = "Plus sin límite", city = "Miami", durationDays = 1 });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    private async Task<List<Guid>> SeedSavedPlans(Guid userId, int count)
    {
        var db = fixture.GetDbContext();
        var ids = new List<Guid>(count);
        for (var i = 0; i < count; i++)
        {
            var plan = new Plan
            {
                Id = Guid.NewGuid(),
                Name = $"Saved {i}",
                City = "Miami",
                Type = "ai",
                DurationDays = 1,
                IsPublic = false,
                CreatedById = userId,
            };
            db.Plans.Add(plan);
            ids.Add(plan.Id);
        }
        await db.SaveChangesAsync();
        return ids;
    }

    [Fact]
    public async Task GetPlans_Anonymous_ReturnsShowcaseOnly()
    {
        var db = fixture.GetDbContext();
        db.Plans.Add(MakePlan("Showcase Only", isShowcase: true));
        db.Plans.Add(MakePlan("Hidden Non-Showcase", isShowcase: false));
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync("/plans");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var p in body.GetProperty("plans").EnumerateArray())
            Assert.True(p.GetProperty("isShowcase").GetBoolean());
    }

    [Fact]
    public async Task GetPlans_Authenticated_ReturnsAllPublic()
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-plans-{userId:N}";
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = $"plans-auth-{userId:N}@test.com", FirebaseUid = firebaseUid });
        var nonShowcase = MakePlan("Auth Visible", isShowcase: false);
        db.Plans.Add(nonShowcase);
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(userId, firebaseUid);
        var response = await client.GetAsync("/plans");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("plans").EnumerateArray()
            .Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(nonShowcase.Id, ids);
    }

    [Fact]
    public async Task GetPlan_ReturnsWithStops()
    {
        var db = fixture.GetDbContext();
        var place = new Place
        {
            Id = Guid.NewGuid(), Name = "Stop Place", Category = "Food",
            WhyThisPlace = "Amazing", Status = "published"
        };
        var plan = MakePlan("Plan With Stops");
        db.Places.Add(place);
        db.Plans.Add(plan);
        db.PlanStops.Add(new PlanStop
        {
            Id = Guid.NewGuid(), PlanId = plan.Id, PlaceId = place.Id,
            DayNumber = 1, OrderIndex = 0, TimeBlock = "morning"
        });
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/plans/{plan.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Plan With Stops", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("days").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task GetPlan_PrivatePlan_OtherUser_Returns404()
    {
        var ownerId = Guid.NewGuid();
        var ownerFbUid = $"fb-owner-{ownerId:N}";
        var otherId = Guid.NewGuid();
        var otherFbUid = $"fb-other-{otherId:N}";
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = ownerId, Email = $"owner-{ownerId:N}@test.com", FirebaseUid = ownerFbUid });
        db.Users.Add(new User { Id = otherId, Email = $"other-{otherId:N}@test.com", FirebaseUid = otherFbUid });
        var privatePlan = MakePlan("Private Plan", isPublic: false, createdById: ownerId);
        db.Plans.Add(privatePlan);
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(otherId, otherFbUid);
        var response = await client.GetAsync($"/plans/{privatePlan.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPlan_PrivatePlan_Owner_ReturnsOk()
    {
        var ownerId = Guid.NewGuid();
        var ownerFbUid = $"fb-privown-{ownerId:N}";
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = ownerId, Email = $"priv-owner-{ownerId:N}@test.com", FirebaseUid = ownerFbUid });
        var privatePlan = MakePlan("Owner Private", isPublic: false, createdById: ownerId);
        db.Plans.Add(privatePlan);
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(ownerId, ownerFbUid);
        var response = await client.GetAsync($"/plans/{privatePlan.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPlans_ResponseShape_MatchesPlanDto()
    {
        var db = fixture.GetDbContext();
        db.Plans.Add(MakePlan("Shape Check Showcase", isShowcase: true));
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync("/plans");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var plans = body.GetProperty("plans").EnumerateArray();
        foreach (var plan in plans)
        {
            // Propiedades requeridas por PlanDto (DTO público).
            Assert.True(plan.TryGetProperty("id", out _));
            Assert.True(plan.TryGetProperty("name", out _));
            Assert.True(plan.TryGetProperty("city", out _));
            Assert.True(plan.TryGetProperty("type", out _));
            Assert.True(plan.TryGetProperty("durationDays", out _));
            Assert.True(plan.TryGetProperty("isPublic", out _));
            Assert.True(plan.TryGetProperty("isShowcase", out _));
            Assert.True(plan.TryGetProperty("createdAt", out _));
            // Navegaciones EF no deben aparecer en el JSON.
            Assert.False(plan.TryGetProperty("stops", out _));
            Assert.False(plan.TryGetProperty("createdBy", out _));
            Assert.False(plan.TryGetProperty("followSessions", out _));
        }
    }

    [Fact]
    public async Task GetPlan_ResponseShape_StopsHavePublicPlaceDto()
    {
        var db = fixture.GetDbContext();
        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = "Shape Stop Place",
            Category = "Food",
            WhyThisPlace = "Amazing",
            Status = "published",
            // Campos de curación internos que NUNCA deben salir en el DTO público.
            RejectionReason = "internal",
            AiVibeScore = 88,
            Flags = new List<string> { "flag-internal" }
        };
        var plan = MakePlan("Plan Shape With Stops");
        db.Places.Add(place);
        db.Plans.Add(plan);
        db.PlanStops.Add(new PlanStop
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            PlaceId = place.Id,
            DayNumber = 1,
            OrderIndex = 0,
            TimeBlock = "morning"
        });
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/plans/{plan.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var day = body.GetProperty("days").EnumerateArray().First();
        var stop = day.GetProperty("stops").EnumerateArray().First();

        Assert.True(stop.TryGetProperty("id", out _));
        Assert.True(stop.TryGetProperty("placeId", out _));
        Assert.True(stop.TryGetProperty("dayNumber", out _));
        Assert.True(stop.TryGetProperty("orderIndex", out _));

        var stopPlace = stop.GetProperty("place");
        Assert.True(stopPlace.TryGetProperty("name", out _));
        Assert.True(stopPlace.TryGetProperty("whyThisPlace", out _));
        // Curación interna NO debe aparecer en el DTO público de Place.
        Assert.False(stopPlace.TryGetProperty("rejectionReason", out _));
        Assert.False(stopPlace.TryGetProperty("aiVibeScore", out _));
        Assert.False(stopPlace.TryGetProperty("submittedById", out _));
        Assert.False(stopPlace.TryGetProperty("reviewedById", out _));
        Assert.False(stopPlace.TryGetProperty("flags", out _));
    }

    private static Plan MakePlan(
        string name,
        bool isPublic = true,
        bool isShowcase = false,
        Guid? createdById = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        City = "Miami",
        Type = "curated",
        IsPublic = isPublic,
        IsShowcase = isShowcase,
        CreatedById = createdById
    };
}
