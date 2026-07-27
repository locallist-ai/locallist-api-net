using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Social S1 (MINOR b): el read-path del follow re-chequea el acceso al plan EN CADA read. Un
/// follow iniciado con acceso (IDOR #116) se REVOCA si el plan pasa a private o el owner bloquea
/// al follower → 403 estructurado plan_access_revoked en GetActiveSession / next / skip. DB real.
/// </summary>
public class FollowAccessRevocationTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<Guid> SeedUser(string tag)
    {
        var id = Guid.NewGuid();
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = id, Email = $"{tag}-{id:N}@test.com", FirebaseUid = $"fb-{tag}-{id:N}" });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Plan> SeedPublicPlanWithStops(Guid ownerId)
    {
        var db = fixture.GetDbContext();
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Name = "Followable", City = "Miami", Type = "custom",
            DurationDays = 1, Visibility = "public", CreatedById = ownerId,
        };
        var place = new Place
        {
            Id = Guid.NewGuid(), Name = "Follow Stop", Category = "Food",
            WhyThisPlace = "Nice", Status = "published"
        };
        db.Plans.Add(plan);
        db.Places.Add(place);
        db.PlanStops.Add(new PlanStop
        {
            Id = Guid.NewGuid(), PlanId = plan.Id, PlaceId = place.Id,
            DayNumber = 1, OrderIndex = 0, TimeBlock = "morning"
        });
        await db.SaveChangesAsync();
        return plan;
    }

    private async Task<(HttpClient client, Guid sessionId)> StartFollow(Guid follower, string tag, Guid planId)
    {
        var client = await fixture.CreateAppAuthenticatedClientWithUser(follower, $"{tag}-{follower:N}@test.com");
        var res = await client.PostAsJsonAsync("/follow/start", new { planId });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (client, body.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task GetActiveSession_AfterPlanTurnedPrivate_Returns403Structured()
    {
        var owner = await SeedUser("revoke-owner");
        var follower = await SeedUser("revoke-follower");
        var plan = await SeedPublicPlanWithStops(owner);
        var (client, _) = await StartFollow(follower, "revoke-f", plan.Id);

        // Antes de revocar, el read funciona.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/follow/active")).StatusCode);

        // El plan pasa a private → el follow en curso se revoca.
        var db = fixture.GetDbContext();
        var p = await db.Plans.FirstAsync(x => x.Id == plan.Id);
        p.Visibility = "private";
        await db.SaveChangesAsync();

        var res = await client.GetAsync("/follow/active");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("plan_access_revoked", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AdvanceNext_AfterPlanTurnedPrivate_Returns403Structured()
    {
        var owner = await SeedUser("revoke-next-owner");
        var follower = await SeedUser("revoke-next-follower");
        var plan = await SeedPublicPlanWithStops(owner);
        var (client, sessionId) = await StartFollow(follower, "revoke-next-f", plan.Id);

        var db = fixture.GetDbContext();
        var p = await db.Plans.FirstAsync(x => x.Id == plan.Id);
        p.Visibility = "private";
        await db.SaveChangesAsync();

        var res = await client.PatchAsync($"/follow/{sessionId}/next", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("plan_access_revoked", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetActiveSession_AfterOwnerBlockedFollower_Returns403Structured()
    {
        var owner = await SeedUser("block-owner");
        var follower = await SeedUser("block-follower");
        var plan = await SeedPublicPlanWithStops(owner);
        var (client, _) = await StartFollow(follower, "block-f", plan.Id);

        // El owner bloquea al follower → PlanAccessService niega CanView → follow revocado.
        var db = fixture.GetDbContext();
        db.UserBlocks.Add(new UserBlock { BlockerId = owner, BlockedId = follower });
        await db.SaveChangesAsync();

        var res = await client.GetAsync("/follow/active");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("plan_access_revoked", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetActiveSession_PlanStillAccessible_Returns200()
    {
        // Control: sin revocación, el read sigue devolviendo 200 (no rompimos el camino feliz).
        var owner = await SeedUser("norevoke-owner");
        var follower = await SeedUser("norevoke-follower");
        var plan = await SeedPublicPlanWithStops(owner);
        var (client, _) = await StartFollow(follower, "norevoke-f", plan.Id);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/follow/active")).StatusCode);
    }
}
