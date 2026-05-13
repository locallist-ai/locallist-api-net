using System.Net;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Integration tests for GET/PUT/DELETE /me/profile.
/// Covers upsert, partial update, delete, auth guard, and profile pre-fill in chat.
/// </summary>
public class ProfileTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // ── GET /me/profile ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetProfile_Unauthenticated_Returns401()
    {
        var client = fixture.CreateClient();
        var response = await client.GetAsync("/me/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetProfile_NoProfile_Returns204()
    {
        var (client, _) = await CreateAuthenticatedClient();
        var response = await client.GetAsync("/me/profile");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task GetProfile_ExistingProfile_ReturnsIt()
    {
        var (client, userId) = await CreateAuthenticatedClient();
        var db = fixture.GetDbContext();
        db.UserProfiles.Add(new UserProfile
        {
            UserId = userId,
            DefaultGroupType = "couple",
            PacePreference = "slow",
            DefaultBudgetTier = "moderate",
            DietaryRestrictions = new List<string> { "vegetarian" },
        });
        await db.SaveChangesAsync();

        var response = await client.GetAsync("/me/profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("couple", body.GetProperty("defaultGroupType").GetString());
        Assert.Equal("slow", body.GetProperty("pacePreference").GetString());
        Assert.Equal("moderate", body.GetProperty("defaultBudgetTier").GetString());
        Assert.Equal("vegetarian", body.GetProperty("dietaryRestrictions")[0].GetString());
    }

    // ── PUT /me/profile ───────────────────────────────────────────────────────

    [Fact]
    public async Task PutProfile_Unauthenticated_Returns401()
    {
        var client = fixture.CreateClient();
        var response = await client.PutAsJsonAsync("/me/profile", new { defaultGroupType = "solo" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PutProfile_CreatesProfile()
    {
        var (client, _) = await CreateAuthenticatedClient();

        var response = await client.PutAsJsonAsync("/me/profile", new
        {
            defaultGroupType = "solo",
            pacePreference = "fast",
            defaultBudgetTier = "budget",
            dietaryRestrictions = new[] { "vegan" },
            favoriteCity = "Miami"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("solo", body.GetProperty("defaultGroupType").GetString());
        Assert.Equal("fast", body.GetProperty("pacePreference").GetString());
        Assert.Equal("Miami", body.GetProperty("favoriteCity").GetString());
    }

    [Fact]
    public async Task PutProfile_UpdatesExistingProfile()
    {
        var (client, userId) = await CreateAuthenticatedClient();
        var db = fixture.GetDbContext();
        db.UserProfiles.Add(new UserProfile
        {
            UserId = userId,
            DefaultGroupType = "couple",
            PacePreference = "slow",
        });
        await db.SaveChangesAsync();

        // Update only pace — groupType should stay
        var response = await client.PutAsJsonAsync("/me/profile", new { pacePreference = "fast" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("couple", body.GetProperty("defaultGroupType").GetString()); // unchanged
        Assert.Equal("fast", body.GetProperty("pacePreference").GetString());     // updated
    }

    [Fact]
    public async Task PutProfile_ThenGet_ReturnsUpdatedData()
    {
        var (client, _) = await CreateAuthenticatedClient();

        await client.PutAsJsonAsync("/me/profile", new
        {
            defaultGroupType = "friends",
            companionTags = new[] { "party" }
        });

        var response = await client.GetAsync("/me/profile");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("friends", body.GetProperty("defaultGroupType").GetString());
        Assert.Equal("party", body.GetProperty("companionTags")[0].GetString());
    }

    // ── DELETE /me/profile ────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteProfile_Unauthenticated_Returns401()
    {
        var client = fixture.CreateClient();
        var response = await client.DeleteAsync("/me/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteProfile_ExistingProfile_Returns204()
    {
        var (client, userId) = await CreateAuthenticatedClient();
        var db = fixture.GetDbContext();
        db.UserProfiles.Add(new UserProfile { UserId = userId, DefaultGroupType = "solo" });
        await db.SaveChangesAsync();

        var response = await client.DeleteAsync("/me/profile");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify it's gone — GET returns 204 when no profile exists
        var getResponse = await client.GetAsync("/me/profile");
        Assert.Equal(HttpStatusCode.NoContent, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteProfile_NoProfile_Returns204()
    {
        var (client, _) = await CreateAuthenticatedClient();
        var response = await client.DeleteAsync("/me/profile");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ── Profile isolation between users ───────────────────────────────────────

    [Fact]
    public async Task Profile_TwoUsers_DontSeeEachOthers()
    {
        var (clientA, _) = await CreateAuthenticatedClient();
        var (clientB, _) = await CreateAuthenticatedClient();

        await clientA.PutAsJsonAsync("/me/profile", new { defaultGroupType = "couple" });
        await clientB.PutAsJsonAsync("/me/profile", new { defaultGroupType = "solo" });

        var bodyA = await (await clientA.GetAsync("/me/profile")).Content.ReadFromJsonAsync<JsonElement>();
        var bodyB = await (await clientB.GetAsync("/me/profile")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("couple", bodyA.GetProperty("defaultGroupType").GetString());
        Assert.Equal("solo", bodyB.GetProperty("defaultGroupType").GetString());
    }

    // ── Chat pre-fill from profile ────────────────────────────────────────────

    [Fact]
    public async Task ChatTurn_WithProfile_PreFillsGroupTypeAndPace()
    {
        var (client, userId) = await CreateAuthenticatedClient();
        fixture.FakeGemini.Responder = _ => BuildFakeGeminiResponse(new
        {
            city = "Miami",
            days = 3,
            groupType = "couple",
            categories = new[] { "food" },
            budget = (string?)null,
            pace = "slow",
            dietary = new string[] { },
            exclusions = new string[] { },
            vibesPrimary = (string?)null,
        }, aiMessage: "Got it! What's your budget?");

        var db = fixture.GetDbContext();
        var profile = new UserProfile
        {
            UserId = userId,
            DefaultGroupType = "couple",
            PacePreference = "slow",
            DefaultBudgetTier = "moderate",
        };
        db.UserProfiles.Add(profile);
        await db.SaveChangesAsync();

        // First turn — session is new, profile should be pre-filled
        var response = await client.PostAsJsonAsync("/chat/turn", new
        {
            sessionId = (Guid?)null,
            message = "3 days in Miami, foodies",
            quickReplyId = (string?)null
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // groupType and pace should already be present (from profile)
        var slots = body.GetProperty("slots");
        Assert.Equal("couple", slots.GetProperty("groupType").GetString());
        Assert.Equal("slow", slots.GetProperty("pace").GetString());
        // budget pre-filled from profile too
        Assert.Equal("moderate", slots.GetProperty("budget").GetString());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(HttpClient client, Guid userId)> CreateAuthenticatedClient()
    {
        var db = fixture.GetDbContext();
        var userId = Guid.NewGuid();
        var email = $"profile-{userId:N}@test.com";
        db.Users.Add(new User { Id = userId, Email = email });
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var token = fixture.CreateAppToken(userId, email);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        return (client, userId);
    }

    private static System.Net.Http.HttpResponseMessage BuildFakeGeminiResponse(object extracted, string aiMessage)
    {
        var text = System.Text.Json.JsonSerializer.Serialize(new
        {
            extracted,
            aiMessage,
            nextQuestion = "budget",
            quickReplies = new[] { new { id = "budget_moderate", label = "Moderate" } }
        });
        var envelope = new
        {
            candidates = new[]
            {
                new { content = new { parts = new[] { new { text } } } }
            }
        };
        return new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(
                System.Text.Json.JsonSerializer.Serialize(envelope),
                System.Text.Encoding.UTF8, "application/json")
        };
    }
}
