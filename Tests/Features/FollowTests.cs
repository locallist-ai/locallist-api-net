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

    [Fact]
    public async Task Start_Unauthenticated_Returns401()
    {
        var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/follow/start", new { planId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
