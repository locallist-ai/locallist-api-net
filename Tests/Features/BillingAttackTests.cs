using LocalList.API.NET.Features.Billing;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests;

/// <summary>
/// Adversarial repro tests for the RevenueCat billing webhook.
/// These document real weaknesses found while attacking feat/iap-backend-tier.
/// </summary>
public class BillingAttackTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public BillingAttackTests(ApiFixture fixture) => _fixture = fixture;

    private record WebhookResult(bool Received, string Outcome);

    private async Task<Guid> SeedUserAsync(string tier = "free")
    {
        var id = Guid.NewGuid();
        var db = _fixture.GetDbContext();
        db.Users.Add(new User
        {
            Id = id,
            Email = $"attack-{id:N}@example.com",
            Tier = tier,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<string> GetTierAsync(Guid userId)
    {
        var db = _fixture.GetDbContext();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.Tier).FirstAsync();
    }

    private static HttpRequestMessage BuildWebhook(object body, string? authHeader)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/revenuecat")
        {
            Content = JsonContent.Create(body),
        };
        if (authHeader is not null)
            req.Headers.TryAddWithoutValidation("Authorization", authHeader);
        return req;
    }

    private static object Event(string id, string type, string appUserId, long ts) => new
    {
        api_version = "1.0",
        @event = new
        {
            id,
            type,
            app_user_id = appUserId,
            entitlement_ids = new[] { "plus" },
            event_timestamp_ms = ts,
            product_id = "com.locallist.plus.monthly",
        },
    };

    /// <summary>
    /// Former repro, now a REGRESSION TEST for the fix. A forged INITIAL_PURCHASE with
    /// event_timestamp_ms pinned to long.MaxValue used to freeze "pro" forever, because the
    /// old code compared payload timestamps to decide reordering and marked every later
    /// EXPIRATION as "stale".
    ///
    /// The fix makes RevenueCat's REST state the source of truth: the payload timestamp no
    /// longer influences the tier, so a genuine expiration (RC now reports inactive) revokes
    /// the entitlement regardless of any forged timestamp.
    /// </summary>
    [Fact]
    public async Task ForgedMaxTimestampGrant_DoesNotFreezePro_GenuineExpirationRevokes()
    {
        var userId = await SeedUserAsync(tier: "free");
        var client = _fixture.CreateClient();

        // The subscriber is genuinely active at RevenueCat when the (forged-timestamp) grant lands.
        _fixture.FakeRevenueCat.ByAppUserId[userId.ToString()] = RevenueCatEntitlementStatus.Active;

        var grant = await client.SendAsync(BuildWebhook(
            Event("attack-grant", "INITIAL_PURCHASE", userId.ToString(), long.MaxValue),
            authHeader: ApiFixture.TestRevenueCatWebhookSecret));
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        Assert.Equal("GrantedPro", (await grant.Content.ReadFromJsonAsync<WebhookResult>())!.Outcome);
        Assert.Equal("pro", await GetTierAsync(userId));

        // The subscription genuinely expires at RevenueCat; a real EXPIRATION arrives with a
        // real (far smaller) timestamp. The tier MUST revoke — the max-timestamp does not freeze it.
        _fixture.FakeRevenueCat.ByAppUserId[userId.ToString()] = RevenueCatEntitlementStatus.Inactive;
        var realNowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expire = await client.SendAsync(BuildWebhook(
            Event("genuine-expire", "EXPIRATION", userId.ToString(), realNowMs),
            authHeader: ApiFixture.TestRevenueCatWebhookSecret));
        Assert.Equal(HttpStatusCode.OK, expire.StatusCode);
        Assert.Equal("RevokedToFree", (await expire.Content.ReadFromJsonAsync<WebhookResult>())!.Outcome);
        Assert.Equal("free", await GetTierAsync(userId));
    }

    /// <summary>
    /// A leaked webhook secret must not let an attacker grant "pro" to an arbitrary victim who
    /// never purchased: RevenueCat's REST state (inactive) overrides the forged payload.
    /// </summary>
    [Fact]
    public async Task ForgedGrantForNonPayingVictim_DoesNotGrantPro()
    {
        var victimId = await SeedUserAsync(tier: "free");
        _fixture.FakeRevenueCat.ByAppUserId[victimId.ToString()] = RevenueCatEntitlementStatus.Inactive;
        var client = _fixture.CreateClient();

        var grant = await client.SendAsync(BuildWebhook(
            Event("attack-victim", "INITIAL_PURCHASE", victimId.ToString(), long.MaxValue),
            authHeader: ApiFixture.TestRevenueCatWebhookSecret));

        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        Assert.Equal("free", await GetTierAsync(victimId));
    }
}
