using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

public class FollowTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Start_CreatesSession()
    {
        var (userId, firebaseUid, planId) = await SeedUserAndPlan();
        var client = fixture.CreateAuthenticatedClient(userId, firebaseUid);

        var response = await client.PostAsJsonAsync("/follow/start", new { planId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("active", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Start_DuplicateActive_ReturnsConflict()
    {
        var (userId, firebaseUid, planId) = await SeedUserAndPlan();
        var client = fixture.CreateAuthenticatedClient(userId, firebaseUid);

        await client.PostAsJsonAsync("/follow/start", new { planId });
        var response = await client.PostAsJsonAsync("/follow/start", new { planId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Pause_PausesSession()
    {
        var (userId, firebaseUid, planId) = await SeedUserAndPlan();
        var client = fixture.CreateAuthenticatedClient(userId, firebaseUid);
        var startRes = await client.PostAsJsonAsync("/follow/start", new { planId });
        var sessionId = (await startRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var response = await client.PatchAsync($"/follow/{sessionId}/pause", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("paused", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Complete_CompletesSession()
    {
        var (userId, firebaseUid, planId) = await SeedUserAndPlan();
        var client = fixture.CreateAuthenticatedClient(userId, firebaseUid);
        var startRes = await client.PostAsJsonAsync("/follow/start", new { planId });
        var sessionId = (await startRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var response = await client.PatchAsync($"/follow/{sessionId}/complete", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Next_AdvancesStop()
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-next-{userId:N}";
        var planId = Guid.NewGuid();
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = $"follow-next-{userId:N}@test.com", FirebaseUid = firebaseUid });
        db.Plans.Add(new Plan { Id = planId, Name = "Multi Stop", City = "Miami", Type = "curated" });
        var placeA = new Place { Id = Guid.NewGuid(), Name = "A", Category = "Food", WhyThisPlace = "x", Status = "published" };
        var placeB = new Place { Id = Guid.NewGuid(), Name = "B", Category = "Food", WhyThisPlace = "y", Status = "published" };
        db.Places.Add(placeA);
        db.Places.Add(placeB);
        db.PlanStops.Add(new PlanStop { Id = Guid.NewGuid(), PlanId = planId, PlaceId = placeA.Id, DayNumber = 1, OrderIndex = 0 });
        db.PlanStops.Add(new PlanStop { Id = Guid.NewGuid(), PlanId = planId, PlaceId = placeB.Id, DayNumber = 1, OrderIndex = 1 });
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(userId, firebaseUid);
        var startRes = await client.PostAsJsonAsync("/follow/start", new { planId });
        var sessionId = (await startRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var response = await client.PatchAsync($"/follow/{sessionId}/next", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("currentStopIndex").GetInt32());
    }

    // ── SEGURIDAD (T2 ronda 2): GET /follow/active NO debe filtrar la API key de Google
    // (Place.Photos con URL places.googleapis.com?...&key=SECRET) ni sobre-exponer los campos
    // internos de curacion al serializar la entidad Place/PlanStop cruda. Repro adversarial. ──
    [Fact]
    public async Task GetActive_StopPlaceHasGoogleKey_ResponseNeverLeaksKey_NorCurationFields()
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-leak-{userId:N}";
        var planId = Guid.NewGuid();
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = $"follow-leak-{userId:N}@test.com", FirebaseUid = firebaseUid });
        db.Plans.Add(new Plan { Id = planId, Name = "Leak Plan", City = "Miami", Type = "curated" });

        // Place Google-importada con la URL con key guardada + campos internos de curacion.
        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = "Keyed Spot",
            Category = "Food",
            City = "Miami",
            WhyThisPlace = "x",
            Status = "published",
            GooglePlaceId = "ChIJ_follow_leak",
            Photos = [PhotoDtoTestData.StoredGoogleUrlWithKey],
            Flags = ["INTERNAL-FLAG-SENTINEL"],
            AiVibeScore = 42,
            RejectionReason = "REJECT-REASON-SENTINEL",
        };
        db.Places.Add(place);
        db.PlanStops.Add(new PlanStop { Id = Guid.NewGuid(), PlanId = planId, PlaceId = place.Id, DayNumber = 1, OrderIndex = 0 });
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(userId, firebaseUid);
        await client.PostAsJsonAsync("/follow/start", new { planId });

        var response = await client.GetAsync("/follow/active");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rawBody = await response.Content.ReadAsStringAsync();
        // La key NUNCA sale.
        Assert.DoesNotContain("googleapis.com", rawBody);
        Assert.DoesNotContain("key=", rawBody);
        Assert.DoesNotContain("SUPER-SECRET-KEY", rawBody);
        // Los campos internos de curacion NUNCA salen.
        Assert.DoesNotContain("INTERNAL-FLAG-SENTINEL", rawBody);
        Assert.DoesNotContain("REJECT-REASON-SENTINEL", rawBody);
        Assert.DoesNotContain("aiVibeScore", rawBody);
        Assert.DoesNotContain("submittedById", rawBody);
        Assert.DoesNotContain("reviewedById", rawBody);
        Assert.DoesNotContain("rejectionReason", rawBody);

        var body = JsonDocument.Parse(rawBody).RootElement;
        // La foto legitima SIGUE viniendo, sintetizada por el proxy (ruta relativa, sin PublicBaseUrl).
        var place1 = body.GetProperty("currentStop").GetProperty("place");
        Assert.Equal($"/places/{place.Id}/photos/0", place1.GetProperty("photos")[0].GetString());
        Assert.Equal("google", place1.GetProperty("photoSource").GetString());
        Assert.False(place1.TryGetProperty("flags", out _));
        // El place embebido en el stop tambien va sintetizado (PlaceDto).
        var nestedInStop = body.GetProperty("currentStop").GetProperty("stop").GetProperty("place");
        Assert.Equal($"/places/{place.Id}/photos/0", nestedInStop.GetProperty("photos")[0].GetString());
    }

    [Fact]
    public async Task Start_Unauthenticated_Returns401()
    {
        var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/follow/start", new { planId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── SEGURIDAD (audit 2026-07-24): IDOR en StartSession. Un user con el GUID de un plan
    // PRIVADO ajeno podia iniciar follow y leer su itinerario via GetActiveSession. StartSession
    // debe rechazar (404) planes privados que no son del caller, igual que PlansController.GetPlan. ──
    [Fact]
    public async Task Start_OtherUsersPrivatePlan_Returns404_AndCreatesNoSession()
    {
        var ownerId = Guid.NewGuid();
        var attackerId = Guid.NewGuid();
        var attackerFbUid = $"fb-idor-{attackerId:N}";
        var planId = Guid.NewGuid();

        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = ownerId, Email = $"owner-{ownerId:N}@test.com", FirebaseUid = $"fb-owner-{ownerId:N}" });
        db.Users.Add(new User { Id = attackerId, Email = $"attacker-{attackerId:N}@test.com", FirebaseUid = attackerFbUid });
        db.Plans.Add(new Plan { Id = planId, Name = "Private Plan", City = "Miami", Type = "personal", IsPublic = false, CreatedById = ownerId });
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(attackerId, attackerFbUid);
        var response = await client.PostAsJsonAsync("/follow/start", new { planId });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var verifyDb = fixture.GetDbContext();
        var sessionExists = verifyDb.FollowSessions.Any(fs => fs.UserId == attackerId);
        Assert.False(sessionExists);
    }

    [Fact]
    public async Task Start_OwnPrivatePlan_CreatesSession()
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-ownpriv-{userId:N}";
        var planId = Guid.NewGuid();

        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = $"ownpriv-{userId:N}@test.com", FirebaseUid = firebaseUid });
        db.Plans.Add(new Plan { Id = planId, Name = "My Private Plan", City = "Miami", Type = "personal", IsPublic = false, CreatedById = userId });
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(userId, firebaseUid);
        var response = await client.PostAsJsonAsync("/follow/start", new { planId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("active", body.GetProperty("status").GetString());
    }

    private async Task<(Guid userId, string firebaseUid, Guid planId)> SeedUserAndPlan()
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-follow-{userId:N}";
        var planId = Guid.NewGuid();
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = $"follow-{userId:N}@test.com", FirebaseUid = firebaseUid });
        db.Plans.Add(new Plan { Id = planId, Name = "Follow Plan", City = "Miami", Type = "curated" });
        await db.SaveChangesAsync();
        return (userId, firebaseUid, planId);
    }
}
