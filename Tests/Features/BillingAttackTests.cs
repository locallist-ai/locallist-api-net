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

    private static object Event(
        string id, string type, string appUserId, long ts, string? originalAppUserId = null) => new
    {
        api_version = "1.0",
        @event = new
        {
            id,
            type,
            app_user_id = appUserId,
            original_app_user_id = originalAppUserId,
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

    /// <summary>
    /// GOD-TOKEN via decoupled identity: the payload names an app_user_id that RevenueCat reports
    /// Active but which maps to NO local user (e.g. an unlinked $RCAnonymousID, or a deleted
    /// account whose RC subscription is still live), and puts the ATTACKER's Guid in
    /// original_app_user_id. The old code verified the first id in RC (Active) but credited the
    /// user resolved from the second (the attacker) → free pro. The fix verifies only the
    /// resolved user's OWN ids, so the attacker (not entitled) is not granted pro.
    /// </summary>
    [Fact]
    public async Task DecoupledIdentity_GodToken_DoesNotCreditUnverifiedUser()
    {
        var attackerId = await SeedUserAsync(tier: "free");
        var client = _fixture.CreateClient();

        // An id RevenueCat reports Active but that does not belong to the attacker.
        const string ghostActiveId = "$RCAnonymousID:ghost-active";
        _fixture.FakeRevenueCat.ByAppUserId[ghostActiveId] = RevenueCatEntitlementStatus.Active;
        // The attacker's own id is NOT entitled (fake default = Inactive).

        var res = await client.SendAsync(BuildWebhook(
            Event("attack-godtoken", "INITIAL_PURCHASE", ghostActiveId, long.MaxValue,
                originalAppUserId: attackerId.ToString()),
            authHeader: ApiFixture.TestRevenueCatWebhookSecret));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("free", await GetTierAsync(attackerId));
    }

    /// <summary>
    /// GRIEFING variant: attacker knows a victim's User.Id + the secret. They send an event whose
    /// app_user_id is garbage (Inactive) and original_app_user_id is the victim's Guid, trying to
    /// downgrade a paying victim. The old code verified the garbage id (Inactive) and credited the
    /// victim → free (downgrade). The fix verifies the victim's OWN id (Active) → stays pro.
    /// </summary>
    [Fact]
    public async Task DecoupledIdentity_Griefing_DoesNotDowngradePayingVictim()
    {
        var victimId = await SeedUserAsync(tier: "pro");
        // Victim genuinely holds an active entitlement at RevenueCat.
        _fixture.FakeRevenueCat.ByAppUserId[victimId.ToString()] = RevenueCatEntitlementStatus.Active;
        var client = _fixture.CreateClient();

        const string garbageInactiveId = "$RCAnonymousID:garbage"; // fake default = Inactive
        var res = await client.SendAsync(BuildWebhook(
            Event("attack-grief", "EXPIRATION", garbageInactiveId, 1000,
                originalAppUserId: victimId.ToString()),
            authHeader: ApiFixture.TestRevenueCatWebhookSecret));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("pro", await GetTierAsync(victimId));
    }

    // A TRANSFER event carries no app_user_id — the entitlement moves between these two arrays.
    private static object TransferEvent(string id, string[] from, string[] to, long ts = 1000) => new
    {
        api_version = "1.0",
        @event = new
        {
            id,
            type = "TRANSFER",
            transferred_from = from,
            transferred_to = to,
            event_timestamp_ms = ts,
            store = "app_store",
        },
    };

    /// <summary>
    /// FORGED TRANSFER (grant): attacker with the leaked secret crafts a TRANSFER naming the victim
    /// as the gainer (transferred_to) and a ghost id RevenueCat reports Active as the loser
    /// (transferred_from). The credit still hangs on verifying the victim's OWN id, which RC reports
    /// inactive → the victim is not granted pro. The forged payload cannot mint what RC denies.
    /// </summary>
    [Fact]
    public async Task ForgedTransfer_ToVictim_DoesNotGrantWhatRcDenies()
    {
        var victimId = await SeedUserAsync(tier: "free");
        _fixture.FakeRevenueCat.ByAppUserId[victimId.ToString()] = RevenueCatEntitlementStatus.Inactive;
        const string ghostActiveId = "$RCAnonymousID:ghost-active"; // Active at RC, no local user
        _fixture.FakeRevenueCat.ByAppUserId[ghostActiveId] = RevenueCatEntitlementStatus.Active;
        var client = _fixture.CreateClient();

        var res = await client.SendAsync(BuildWebhook(
            TransferEvent("attack-transfer-grant", new[] { ghostActiveId }, new[] { victimId.ToString() }),
            authHeader: ApiFixture.TestRevenueCatWebhookSecret));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("free", await GetTierAsync(victimId));
    }

    /// <summary>
    /// FORGED TRANSFER (griefing): attacker puts a genuinely-paying victim in transferred_from with
    /// a garbage gainer, hoping the transfer revokes the victim. The victim's OWN id is re-verified
    /// against RevenueCat (Active) → the victim stays pro. A forged transfer cannot strip an active
    /// subscriber.
    /// </summary>
    [Fact]
    public async Task ForgedTransfer_FromPayingVictim_DoesNotDowngradeWhatRcConfirms()
    {
        var victimId = await SeedUserAsync(tier: "pro");
        _fixture.FakeRevenueCat.ByAppUserId[victimId.ToString()] = RevenueCatEntitlementStatus.Active;
        const string ghostId = "$RCAnonymousID:ghost"; // fake default = Inactive, no local user
        var client = _fixture.CreateClient();

        var res = await client.SendAsync(BuildWebhook(
            TransferEvent("attack-transfer-grief", new[] { victimId.ToString() }, new[] { ghostId }),
            authHeader: ApiFixture.TestRevenueCatWebhookSecret));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("pro", await GetTierAsync(victimId));
    }
}
