using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

public class PlansTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
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
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = $"plans-auth-{userId:N}@test.com" });
        var nonShowcase = MakePlan("Auth Visible", isShowcase: false);
        db.Plans.Add(nonShowcase);
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(userId);
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
        var otherId = Guid.NewGuid();
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = ownerId, Email = $"owner-{ownerId:N}@test.com" });
        db.Users.Add(new User { Id = otherId, Email = $"other-{otherId:N}@test.com" });
        var privatePlan = MakePlan("Private Plan", isPublic: false, createdById: ownerId);
        db.Plans.Add(privatePlan);
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(otherId);
        var response = await client.GetAsync($"/plans/{privatePlan.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPlan_PrivatePlan_Owner_ReturnsOk()
    {
        var ownerId = Guid.NewGuid();
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = ownerId, Email = $"priv-owner-{ownerId:N}@test.com" });
        var privatePlan = MakePlan("Owner Private", isPublic: false, createdById: ownerId);
        db.Plans.Add(privatePlan);
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(ownerId);
        var response = await client.GetAsync($"/plans/{privatePlan.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
