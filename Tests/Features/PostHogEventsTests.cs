using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Integration tests: verifica que los eventos PostHog se disparan en los flujos clave.
/// Las llamadas son fire-and-forget, por lo que los tests esperan un tick antes de assertar.
/// </summary>
[Collection("Api")]
public class PostHogEventsTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public PostHogEventsTests(ApiFixture fixture)
    {
        _fixture = fixture;
        _fixture.FakePostHog.Reset();
    }

    // ── Waitlist ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Waitlist_Join_FiresWaitlistJoinedEvent()
    {
        var client = _fixture.CreateClient();
        var email = $"posthog-waitlist-{Guid.NewGuid():N}@test.com";

        var response = await client.PostAsJsonAsync("/waitlist", new
        {
            email,
            anonymousId = "anon-uuid-1234"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await Task.Delay(200);

        Assert.True(_fixture.FakePostHog.HasEvent("waitlist_joined"),
            "Expected waitlist_joined event to be captured");
    }

    // ── Auth: register ────────────────────────────────────────────────────────

    [Fact]
    public async Task Auth_Register_FiresUserSignedUpEvent()
    {
        var client = _fixture.CreateClient();
        var email = $"posthog-reg-{Guid.NewGuid():N}@test.com";

        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "Passw0rd!",
            name = "PostHog Tester",
            anonymousId = "anon-test-reg"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Task.Delay(200);

        Assert.True(_fixture.FakePostHog.HasEvent("user_signed_up"),
            "Expected user_signed_up event after registration");
    }

    [Fact]
    public async Task Auth_Register_WithAnonymousId_FiresAliasEvent()
    {
        var client = _fixture.CreateClient();
        var email = $"posthog-alias-{Guid.NewGuid():N}@test.com";

        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "Passw0rd!",
            anonymousId = "alias-anon-test"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Task.Delay(200);

        var aliasEvent = _fixture.FakePostHog.ParsedEvents()
            .FirstOrDefault(d =>
                d.RootElement.TryGetProperty("event", out var ev) &&
                ev.GetString() == "$create_alias");

        Assert.NotNull(aliasEvent);
        var alias = aliasEvent!.RootElement
            .GetProperty("properties")
            .GetProperty("alias")
            .GetString();
        Assert.Equal("alias-anon-test", alias);

        foreach (var doc in _fixture.FakePostHog.ParsedEvents()) doc.Dispose();
    }

    // ── Auth: login ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Auth_Login_FiresUserSignedInEvent()
    {
        var client = _fixture.CreateClient();
        var email = $"posthog-login-{Guid.NewGuid():N}@test.com";
        var db = _fixture.GetDbContext();
        db.Users.Add(new User
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Passw0rd!", 4),
            Role = "user"
        });
        await db.SaveChangesAsync();

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            email,
            password = "Passw0rd!"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Task.Delay(200);

        Assert.True(_fixture.FakePostHog.HasEvent("user_signed_in"),
            "Expected user_signed_in event after login");
    }

    // ── Plans: open ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Plans_GetById_Authenticated_FiresPlanOpenedEvent()
    {
        var userId = Guid.NewGuid();
        var client = await _fixture.CreateAuthenticatedClientWithUser(userId, Guid.NewGuid().ToString());
        var db = _fixture.GetDbContext();

        var plan = new Plan
        {
            Name = "PostHog Plan",
            City = "Miami",
            Type = "ai",
            DurationDays = 2,
            IsPublic = true,
            IsShowcase = true,
            CreatedById = userId
        };
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var response = await client.GetAsync($"/plans/{plan.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Task.Delay(200);

        var ev = _fixture.FakePostHog.ParsedEvents()
            .FirstOrDefault(d =>
                d.RootElement.TryGetProperty("event", out var e) && e.GetString() == "plan_opened");

        Assert.NotNull(ev);
        var planId = ev!.RootElement
            .GetProperty("properties")
            .GetProperty("plan_id")
            .GetString();
        Assert.Equal(plan.Id.ToString(), planId);

        foreach (var doc in _fixture.FakePostHog.ParsedEvents()) doc.Dispose();
    }

    // ── Follow: start / complete ──────────────────────────────────────────────

    [Fact]
    public async Task Follow_Start_FiresFollowStartedEvent()
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-posthog-{userId:N}";
        var db = _fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = $"posthog-follow-{userId:N}@test.com", FirebaseUid = firebaseUid });
        var plan = new Plan { Name = "PostHog Follow Plan", City = "Rome", Type = "ai", DurationDays = 1 };
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var client = _fixture.CreateAuthenticatedClient(userId, firebaseUid);

        var response = await client.PostAsJsonAsync("/follow/start", new { planId = plan.Id });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await Task.Delay(200);

        Assert.True(_fixture.FakePostHog.HasEvent("follow_started"),
            "Expected follow_started event after starting follow session");
    }

    [Fact]
    public async Task Follow_Complete_FiresFollowCompletedEvent()
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-posthog-complete-{userId:N}";
        var db = _fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = $"posthog-complete-{userId:N}@test.com", FirebaseUid = firebaseUid });
        var plan = new Plan { Name = "PostHog Complete Plan", City = "Paris", Type = "ai", DurationDays = 1 };
        db.Plans.Add(plan);
        var session = new FollowSession
        {
            UserId = userId,
            PlanId = plan.Id,
            Status = "active",
            CurrentDayIndex = 1,
            CurrentStopIndex = 0
        };
        db.FollowSessions.Add(session);
        await db.SaveChangesAsync();

        _fixture.FakePostHog.Reset();
        var client = _fixture.CreateAuthenticatedClient(userId, firebaseUid);

        var response = await client.PatchAsync($"/follow/{session.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Task.Delay(200);

        Assert.True(_fixture.FakePostHog.HasEvent("follow_completed"),
            "Expected follow_completed event after completing follow session");
    }
}
