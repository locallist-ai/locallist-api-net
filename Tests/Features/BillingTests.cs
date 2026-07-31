using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using LocalList.API.NET.Features.Billing;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalList.API.Tests;

public class BillingTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public BillingTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    private record WebhookResult(bool Received, string Outcome);

    // ---- helpers -----------------------------------------------------------

    private async Task<Guid> SeedUserAsync(string tier = "free", string? rcCustomerId = null)
    {
        var id = Guid.NewGuid();
        var db = _fixture.GetDbContext();
        db.Users.Add(new User
        {
            Id = id,
            Email = $"billing-{id:N}@example.com",
            Tier = tier,
            RcCustomerId = rcCustomerId,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<string> GetTierAsync(Guid userId)
    {
        var db = _fixture.GetDbContext();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.Tier).FirstAsync();
    }

    private static HttpRequestMessage BuildWebhook(
        object body, string? authHeader = ApiFixture.TestRevenueCatWebhookSecret)
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
        string id, string type, string appUserId, long ts,
        string[]? entitlements = null, string? originalAppUserId = null) => new
        {
            api_version = "1.0",
            @event = new
            {
                id,
                type,
                app_user_id = appUserId,
                original_app_user_id = originalAppUserId,
                entitlement_ids = entitlements ?? new[] { "plus" },
                event_timestamp_ms = ts,
                product_id = "com.locallist.plus.monthly",
            },
        };

    private void RcActive(Guid userId) =>
        _fixture.FakeRevenueCat.ByAppUserId[userId.ToString()] = RevenueCatEntitlementStatus.Active;

    // A TRANSFER event carries no app_user_id — the entitlement moves between these arrays.
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

    // ---- webhook auth ------------------------------------------------------

    [Fact]
    public async Task Webhook_InvalidAuthHeader_Returns401AndDoesNotWriteTier()
    {
        var userId = await SeedUserAsync();
        var client = _fixture.CreateClient();

        var req = BuildWebhook(
            Event("evt-bad-auth", "INITIAL_PURCHASE", userId.ToString(), 1000),
            authHeader: "wrong-secret");
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("free", await GetTierAsync(userId));
    }

    [Fact]
    public async Task Webhook_MissingAuthHeader_Returns401()
    {
        var userId = await SeedUserAsync();
        var client = _fixture.CreateClient();

        var req = BuildWebhook(
            Event("evt-no-auth", "INITIAL_PURCHASE", userId.ToString(), 1000),
            authHeader: null);
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("free", await GetTierAsync(userId));
    }

    [Fact]
    public async Task Webhook_MalformedEvent_Returns400()
    {
        var client = _fixture.CreateClient();
        // Valid auth but no event object.
        var req = BuildWebhook(new { api_version = "1.0" });
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---- tier writes -------------------------------------------------------

    [Fact]
    public async Task Webhook_InitialPurchase_WritesProTier_WhenRcConfirmsOwnId()
    {
        var userId = await SeedUserAsync();
        // RevenueCat confirms THIS user's own id (== app_user_id) is entitled.
        RcActive(userId);
        var client = _fixture.CreateClient();

        var req = BuildWebhook(Event("evt-purchase", "INITIAL_PURCHASE", userId.ToString(), 1000));
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<WebhookResult>();
        Assert.Equal("GrantedPro", body!.Outcome);

        var db = _fixture.GetDbContext();
        var user = await db.Users.FirstAsync(u => u.Id == userId);
        Assert.Equal("pro", user.Tier);
    }

    [Fact]
    public async Task Webhook_Expiration_RevertsToFree_WhenRcInactive()
    {
        var userId = await SeedUserAsync(tier: "pro", rcCustomerId: null);
        // RevenueCat authoritatively reports the entitlement as gone.
        _fixture.FakeRevenueCat.ByAppUserId[userId.ToString()] = RevenueCatEntitlementStatus.Inactive;
        var client = _fixture.CreateClient();

        var req = BuildWebhook(Event("evt-expire", "EXPIRATION", userId.ToString(), 2000));
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<WebhookResult>();
        Assert.Equal("RevokedToFree", body!.Outcome);
        Assert.Equal("free", await GetTierAsync(userId));
    }

    [Fact]
    public async Task Webhook_Cancellation_KeepsPro_WhileRcStillActive()
    {
        var userId = await SeedUserAsync(tier: "pro");
        // CANCELLATION = auto-renew off; RevenueCat still reports the entitlement active until
        // expiration, so the tier must stay pro (driven by RC state, not the event type).
        _fixture.FakeRevenueCat.ByAppUserId[userId.ToString()] = RevenueCatEntitlementStatus.Active;
        var client = _fixture.CreateClient();

        var req = BuildWebhook(Event("evt-cancel", "CANCELLATION", userId.ToString(), 1500));
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<WebhookResult>();
        Assert.Equal("GrantedPro", body!.Outcome);
        Assert.Equal("pro", await GetTierAsync(userId));
    }

    [Fact]
    public async Task Webhook_PayloadClaimsPlus_ButRcSaysInactive_DoesNotGrantPro()
    {
        // The payload asserts the "plus" entitlement, but RevenueCat's authoritative state says
        // inactive. We trust RC, not the payload → no pro.
        var userId = await SeedUserAsync();
        _fixture.FakeRevenueCat.ByAppUserId[userId.ToString()] = RevenueCatEntitlementStatus.Inactive;
        var client = _fixture.CreateClient();

        var req = BuildWebhook(Event("evt-payload-lies", "INITIAL_PURCHASE", userId.ToString(), 1000));
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<WebhookResult>();
        Assert.Equal("RevokedToFree", body!.Outcome);
        Assert.Equal("free", await GetTierAsync(userId));
    }

    [Fact]
    public async Task Webhook_MapsByRcCustomerId_WhenAppUserIdIsNotGuid()
    {
        var rcId = $"rcbilling_{Guid.NewGuid():N}";
        var userId = await SeedUserAsync(rcCustomerId: rcId);
        // The user's OWN linked RcCustomerId is the entitled identifier at RevenueCat.
        _fixture.FakeRevenueCat.ByAppUserId[rcId] = RevenueCatEntitlementStatus.Active;
        var client = _fixture.CreateClient();

        var req = BuildWebhook(Event("evt-by-rc", "INITIAL_PURCHASE", rcId, 1000));
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("pro", await GetTierAsync(userId));
    }

    // ---- analytics columns (best-effort, side-effect-free) -----------------

    [Fact]
    public async Task Webhook_PersistsAnalyticsColumns_FromPayload()
    {
        // The processor must copy the analytics fields from the webhook onto the ledger row it
        // already persists — WITHOUT changing dedup/tier/idempotency (asserted elsewhere). Post two
        // representative events covering all nine new columns and assert they land.
        var userId = await SeedUserAsync();
        RcActive(userId);
        var client = _fixture.CreateClient();

        // A) RENEWAL that is a trial conversion, with price/currency/store/product/country/period.
        var renewal = new
        {
            api_version = "1.0",
            @event = new
            {
                id = "evt-analytics-renewal",
                type = "RENEWAL",
                app_user_id = userId.ToString(),
                event_timestamp_ms = 1000L,
                product_id = "com.locallist.plus.monthly",
                period_type = "NORMAL",
                country_code = "US",
                price = 9.99,
                price_in_purchased_currency = 9.49,
                currency = "USD",
                store = "APP_STORE",
                is_trial_conversion = true,
            },
        };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(BuildWebhook(renewal))).StatusCode);

        // B) CANCELLATION carrying a cancel_reason.
        var cancel = new
        {
            api_version = "1.0",
            @event = new
            {
                id = "evt-analytics-cancel",
                type = "CANCELLATION",
                app_user_id = userId.ToString(),
                event_timestamp_ms = 2000L,
                product_id = "com.locallist.plus.monthly",
                country_code = "US",
                store = "APP_STORE",
                cancel_reason = "CUSTOMER_SUPPORT",
            },
        };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(BuildWebhook(cancel))).StatusCode);

        var db = _fixture.GetDbContext();
        var a = await db.BillingEvents.SingleAsync(be => be.RcEventId == "evt-analytics-renewal");
        Assert.Equal("com.locallist.plus.monthly", a.ProductId);
        Assert.Equal("NORMAL", a.PeriodType);
        Assert.Equal("US", a.CountryCode);
        Assert.Equal(9.99m, a.Price);
        Assert.Equal(9.49m, a.PriceInPurchasedCurrency);
        Assert.Equal("USD", a.Currency);
        Assert.Equal("APP_STORE", a.Store);
        Assert.True(a.IsTrialConversion);

        var c = await db.BillingEvents.SingleAsync(be => be.RcEventId == "evt-analytics-cancel");
        Assert.Equal("CUSTOMER_SUPPORT", c.CancelReason);
        Assert.Null(c.Price);              // absent field stays null
        Assert.Null(c.IsTrialConversion);  // absent field stays null
    }

    [Fact]
    public async Task Webhook_MalformedAnalyticsField_DoesNotDropTierCriticalEvent()
    {
        // REGRESSION GUARD: the analytics fields are UNTRUSTED. A type-mismatched value must degrade
        // that ONE field to null and MUST NOT abort deserialization → a 400 would permanently drop
        // the event (only 503 makes RevenueCat re-deliver), so a paying user could miss Plus.
        var userId = await SeedUserAsync();
        RcActive(userId);
        var client = _fixture.CreateClient();

        // Garbage shapes across a money field (object), another money field (array), and the bool.
        var body = new
        {
            api_version = "1.0",
            @event = new
            {
                id = "evt-malformed-analytics",
                type = "INITIAL_PURCHASE",
                app_user_id = userId.ToString(),
                event_timestamp_ms = 1000L,
                product_id = "com.locallist.plus.monthly",
                price = new { nonsense = true },          // object where a number is expected
                price_in_purchased_currency = new[] { 1, 2 }, // array where a number is expected
                is_trial_conversion = "not-a-bool",       // string where a bool is expected
                country_code = "US",
            },
        };
        var res = await client.SendAsync(BuildWebhook(body));

        // The tier-critical event still processes (NOT 400/500) and the tier is applied.
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("GrantedPro", (await res.Content.ReadFromJsonAsync<WebhookResult>())!.Outcome);
        Assert.Equal("pro", await GetTierAsync(userId));

        // The malformed analytics fields persist as null; the well-formed ones survive.
        var db = _fixture.GetDbContext();
        var row = await db.BillingEvents.SingleAsync(be => be.RcEventId == "evt-malformed-analytics");
        Assert.Null(row.Price);
        Assert.Null(row.PriceInPurchasedCurrency);
        Assert.Null(row.IsTrialConversion);
        Assert.Equal("US", row.CountryCode);
        Assert.Equal("com.locallist.plus.monthly", row.ProductId);
    }

    [Fact]
    public async Task Webhook_OverLengthAnalyticsField_TruncatesAndKeepsTierCriticalEvent()
    {
        // REGRESSION GUARD (2026-07-29 audit): the LenientJsonConverters degrade a wrong-TYPE value
        // to null but do NOT bound LENGTH. A string longer than its varchar column would throw
        // Postgres 22001 on SaveChangesAsync and abort the tier-critical persist → a 500 that drops
        // the event (only 503 makes RevenueCat re-deliver). The processor must TRUNCATE each
        // analytics string to its column width so the row always fits and the tier still lands.
        var userId = await SeedUserAsync();
        RcActive(userId);
        var client = _fixture.CreateClient();

        var longProductId = new string('p', 300);  // column is varchar(255)
        var longCountry = new string('C', 40);      // column is varchar(8)
        var body = new
        {
            api_version = "1.0",
            @event = new
            {
                id = "evt-overlength-analytics",
                type = "INITIAL_PURCHASE",
                app_user_id = userId.ToString(),
                event_timestamp_ms = 1000L,
                product_id = longProductId,
                country_code = longCountry,
                store = "APP_STORE",
            },
        };
        var res = await client.SendAsync(BuildWebhook(body));

        // The tier-critical event still processes (NOT 500/dropped) and the tier is applied.
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("GrantedPro", (await res.Content.ReadFromJsonAsync<WebhookResult>())!.Outcome);
        Assert.Equal("pro", await GetTierAsync(userId));

        // The over-length analytics values persist TRUNCATED to their column widths, not dropped.
        var db = _fixture.GetDbContext();
        var row = await db.BillingEvents.SingleAsync(be => be.RcEventId == "evt-overlength-analytics");
        Assert.Equal(255, row.ProductId!.Length);
        Assert.Equal(longProductId[..255], row.ProductId);
        Assert.Equal(8, row.CountryCode!.Length);
        Assert.Equal("APP_STORE", row.Store); // in-bounds value survives intact
    }

    [Fact]
    public async Task Webhook_OverflowingAnalyticsPrice_RetriesWithAnalyticsNulled_AndStillGrantsPro()
    {
        // Covers the SaveLedgerAsync defense-in-depth retry branch. Truncation can't help a numeric
        // overflow: price/price_in_purchased_currency are numeric(12,4), so a value with >8 integer
        // digits throws Postgres 22003 on SaveChangesAsync (not 22001, not the 23505 dedup race).
        // The processor must retry ONCE with the analytics columns nulled so the tier write still
        // commits — best-effort analytics must never lose the tier-critical event.
        var userId = await SeedUserAsync();
        RcActive(userId);
        var client = _fixture.CreateClient();

        var body = new
        {
            api_version = "1.0",
            @event = new
            {
                id = "evt-overflow-price",
                type = "INITIAL_PURCHASE",
                app_user_id = userId.ToString(),
                event_timestamp_ms = 1000L,
                product_id = "com.locallist.plus.monthly",
                price = 123456789012345.67, // overflows numeric(12,4) → PG 22003 on first save
                country_code = "US",
            },
        };
        var res = await client.SendAsync(BuildWebhook(body));

        // The tier-critical event still processes (retry path) and the tier is applied.
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("GrantedPro", (await res.Content.ReadFromJsonAsync<WebhookResult>())!.Outcome);
        Assert.Equal("pro", await GetTierAsync(userId));

        // The retry nulled ALL analytics columns to make the row persist; tier write survived.
        var db = _fixture.GetDbContext();
        var row = await db.BillingEvents.SingleAsync(be => be.RcEventId == "evt-overflow-price");
        Assert.Null(row.Price);
        Assert.Null(row.CountryCode);  // nulled by the retry, even though it was in-bounds
        Assert.Null(row.ProductId);
    }

    [Fact]
    public async Task Webhook_NegativeEventTimestamp_ClampedToNow_NotPre1970()
    {
        // ClampTimestamp used to floor a negative event_timestamp_ms at 0 (== 1970-01-01), which
        // would land the admin daily-series bucket in a pre-1970 date. A negative value must clamp
        // to "now" (like the absurdly-future cap) so it can never produce a 1970 bucket.
        var userId = await SeedUserAsync();
        RcActive(userId);
        var client = _fixture.CreateClient();

        var body = new
        {
            api_version = "1.0",
            @event = new
            {
                id = "evt-negative-ts",
                type = "INITIAL_PURCHASE",
                app_user_id = userId.ToString(),
                event_timestamp_ms = -5000L,
                product_id = "com.locallist.plus.monthly",
            },
        };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(BuildWebhook(body))).StatusCode);

        var db = _fixture.GetDbContext();
        var row = await db.BillingEvents.SingleAsync(be => be.RcEventId == "evt-negative-ts");
        Assert.Equal(_fixture.FakeTime.GetUtcNow().ToUnixTimeMilliseconds(), row.EventTimestampMs);
        Assert.True(row.EventTimestampMs > 1_577_836_800_000L, "must be clamped to now, not 1970");
    }

    // ---- idempotency + reorder --------------------------------------------

    [Fact]
    public async Task Webhook_DuplicateEventId_IsIdempotent()
    {
        var userId = await SeedUserAsync();
        RcActive(userId);
        var client = _fixture.CreateClient();
        const string eventId = "evt-dup";

        var first = await client.SendAsync(BuildWebhook(
            Event(eventId, "INITIAL_PURCHASE", userId.ToString(), 1000)));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.SendAsync(BuildWebhook(
            Event(eventId, "INITIAL_PURCHASE", userId.ToString(), 1000)));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<WebhookResult>();
        Assert.Equal("Duplicate", body!.Outcome);

        Assert.Equal("pro", await GetTierAsync(userId));

        var db = _fixture.GetDbContext();
        var rows = await db.BillingEvents.CountAsync(be => be.RcEventId == eventId);
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task Webhook_OldExpirationForActiveSubscriber_DoesNotDowngrade()
    {
        // A genuinely-active subscriber (RevenueCat says active) whose OLD, out-of-order
        // EXPIRATION webhook is delivered late. Because the tier is re-derived from RC state on
        // every event, the stale delivery re-confirms pro instead of downgrading — no dependence
        // on a payload timestamp guard.
        var userId = await SeedUserAsync();
        _fixture.FakeRevenueCat.ByAppUserId[userId.ToString()] = RevenueCatEntitlementStatus.Active;
        var client = _fixture.CreateClient();

        var renewal = await client.SendAsync(BuildWebhook(
            Event("evt-renew", "RENEWAL", userId.ToString(), 2000)));
        Assert.Equal(HttpStatusCode.OK, renewal.StatusCode);
        Assert.Equal("pro", await GetTierAsync(userId));

        // Old EXPIRATION arrives out of order but RC still reports active → stays pro.
        var expire = await client.SendAsync(BuildWebhook(
            Event("evt-old-expire", "EXPIRATION", userId.ToString(), 1000)));
        Assert.Equal(HttpStatusCode.OK, expire.StatusCode);
        var body = await expire.Content.ReadFromJsonAsync<WebhookResult>();
        Assert.Equal("GrantedPro", body!.Outcome);
        Assert.Equal("pro", await GetTierAsync(userId));
    }

    [Fact]
    public async Task Webhook_ForgedAppUserId_NotBackedByRc_DoesNotGrantPro()
    {
        // Attacker with only the shared secret forges a grant naming a victim who never
        // purchased. RevenueCat reports the victim inactive → no pro.
        var victimId = await SeedUserAsync();
        _fixture.FakeRevenueCat.ByAppUserId[victimId.ToString()] = RevenueCatEntitlementStatus.Inactive;
        var client = _fixture.CreateClient();

        var req = BuildWebhook(Event("evt-forged", "INITIAL_PURCHASE", victimId.ToString(), 1000));
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("free", await GetTierAsync(victimId));
    }

    [Fact]
    public async Task Webhook_RcUnavailable_Returns503_DoesNotRecord_AndRetrySucceeds()
    {
        var userId = await SeedUserAsync();
        _fixture.FakeRevenueCat.ByAppUserId[userId.ToString()] = RevenueCatEntitlementStatus.Unavailable;
        var client = _fixture.CreateClient();
        const string eventId = "evt-rc-down";

        // First delivery: RC unreachable → 503, tier untouched, event NOT recorded (retryable).
        var first = await client.SendAsync(BuildWebhook(
            Event(eventId, "INITIAL_PURCHASE", userId.ToString(), 1000)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal("free", await GetTierAsync(userId));

        var db = _fixture.GetDbContext();
        Assert.Equal(0, await db.BillingEvents.CountAsync(be => be.RcEventId == eventId));

        // RevenueCat recovers and reports active; RevenueCat re-delivers the SAME event id.
        _fixture.FakeRevenueCat.ByAppUserId[userId.ToString()] = RevenueCatEntitlementStatus.Active;
        var retry = await client.SendAsync(BuildWebhook(
            Event(eventId, "INITIAL_PURCHASE", userId.ToString(), 1000)));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("GrantedPro", (await retry.Content.ReadFromJsonAsync<WebhookResult>())!.Outcome);
        Assert.Equal("pro", await GetTierAsync(userId));
    }

    // ---- TRANSFER events ---------------------------------------------------

    [Fact]
    public async Task Webhook_Transfer_MovesEntitlement_OriginToFree_DestinationToPro()
    {
        var originId = await SeedUserAsync(tier: "pro");
        var destId = await SeedUserAsync(tier: "free");
        // After the transfer, RevenueCat's authoritative state: origin no longer entitled, dest is.
        _fixture.FakeRevenueCat.ByAppUserId[originId.ToString()] = RevenueCatEntitlementStatus.Inactive;
        _fixture.FakeRevenueCat.ByAppUserId[destId.ToString()] = RevenueCatEntitlementStatus.Active;
        var client = _fixture.CreateClient();

        var res = await client.SendAsync(BuildWebhook(TransferEvent(
            "evt-transfer-happy", new[] { originId.ToString() }, new[] { destId.ToString() })));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Transferred", (await res.Content.ReadFromJsonAsync<WebhookResult>())!.Outcome);
        Assert.Equal("free", await GetTierAsync(originId));
        Assert.Equal("pro", await GetTierAsync(destId));

        // Exactly one ledger row for the event, attributed to the resolved destination.
        var db = _fixture.GetDbContext();
        var row = await db.BillingEvents.SingleAsync(be => be.RcEventId == "evt-transfer-happy");
        Assert.Equal(destId, row.UserId);
        Assert.Equal("TRANSFER", row.EventType);
    }

    [Fact]
    public async Task Webhook_Transfer_DestinationNotRegistered_StillRevokesOrigin()
    {
        var originId = await SeedUserAsync(tier: "pro");
        _fixture.FakeRevenueCat.ByAppUserId[originId.ToString()] = RevenueCatEntitlementStatus.Inactive;
        var unknownDest = Guid.NewGuid().ToString(); // never seeded
        var client = _fixture.CreateClient();

        var res = await client.SendAsync(BuildWebhook(TransferEvent(
            "evt-transfer-nodest", new[] { originId.ToString() }, new[] { unknownDest })));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Transferred", (await res.Content.ReadFromJsonAsync<WebhookResult>())!.Outcome);
        // The resolved origin is revoked even though the gainer maps to no local user.
        Assert.Equal("free", await GetTierAsync(originId));

        // Row attributed to the origin (destination unresolved).
        var db = _fixture.GetDbContext();
        var row = await db.BillingEvents.SingleAsync(be => be.RcEventId == "evt-transfer-nodest");
        Assert.Equal(originId, row.UserId);
    }

    [Fact]
    public async Task Webhook_Transfer_RcUnavailableMidway_Returns503_NoPartialState_ThenRetrySucceeds()
    {
        var originId = await SeedUserAsync(tier: "pro");
        var destId = await SeedUserAsync(tier: "free");
        // Origin is verifiable (inactive) but RevenueCat is down for the destination.
        _fixture.FakeRevenueCat.ByAppUserId[originId.ToString()] = RevenueCatEntitlementStatus.Inactive;
        _fixture.FakeRevenueCat.ByAppUserId[destId.ToString()] = RevenueCatEntitlementStatus.Unavailable;
        var client = _fixture.CreateClient();
        const string eventId = "evt-transfer-rcdown";

        var first = await client.SendAsync(BuildWebhook(TransferEvent(
            eventId, new[] { originId.ToString() }, new[] { destId.ToString() })));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        // No half-applied transfer: origin NOT revoked, destination NOT granted, nothing recorded.
        Assert.Equal("pro", await GetTierAsync(originId));
        Assert.Equal("free", await GetTierAsync(destId));
        var db = _fixture.GetDbContext();
        Assert.Equal(0, await db.BillingEvents.CountAsync(be => be.RcEventId == eventId));

        // RevenueCat recovers; the SAME event id is re-delivered and now applies in full.
        _fixture.FakeRevenueCat.ByAppUserId[destId.ToString()] = RevenueCatEntitlementStatus.Active;
        var retry = await client.SendAsync(BuildWebhook(TransferEvent(
            eventId, new[] { originId.ToString() }, new[] { destId.ToString() })));

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("free", await GetTierAsync(originId));
        Assert.Equal("pro", await GetTierAsync(destId));
    }

    [Fact]
    public async Task Webhook_Transfer_IsIdempotentOnReplay()
    {
        var originId = await SeedUserAsync(tier: "pro");
        var destId = await SeedUserAsync(tier: "free");
        _fixture.FakeRevenueCat.ByAppUserId[originId.ToString()] = RevenueCatEntitlementStatus.Inactive;
        _fixture.FakeRevenueCat.ByAppUserId[destId.ToString()] = RevenueCatEntitlementStatus.Active;
        var client = _fixture.CreateClient();
        const string eventId = "evt-transfer-dup";

        var first = await client.SendAsync(BuildWebhook(TransferEvent(
            eventId, new[] { originId.ToString() }, new[] { destId.ToString() })));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.SendAsync(BuildWebhook(TransferEvent(
            eventId, new[] { originId.ToString() }, new[] { destId.ToString() })));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("Duplicate", (await second.Content.ReadFromJsonAsync<WebhookResult>())!.Outcome);

        var db = _fixture.GetDbContext();
        Assert.Equal(1, await db.BillingEvents.CountAsync(be => be.RcEventId == eventId));
        Assert.Equal("free", await GetTierAsync(originId));
        Assert.Equal("pro", await GetTierAsync(destId));
    }

    // ---- RequirePro guard (re-queries DB, ignores JWT claim) ---------------

    private static ClaimsPrincipal AuthenticatedPrincipal(Guid userId, string? tierClaim = null)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, userId.ToString()) };
        if (tierClaim is not null) claims.Add(new Claim("tier", tierClaim));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestAuth"));
    }

    private static AuthorizationFilterContext BuildAuthContext(ClaimsPrincipal principal)
    {
        var httpContext = new DefaultHttpContext { User = principal };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());
    }

    [Fact]
    public async Task RequirePro_AllowsProUser()
    {
        var userId = await SeedUserAsync(tier: "pro");
        var db = _fixture.GetDbContext();
        var filter = new RequireProAuthorizationFilter(db, NullLogger<RequireProAuthorizationFilter>.Instance);

        var ctx = BuildAuthContext(AuthenticatedPrincipal(userId));
        await filter.OnAuthorizationAsync(ctx);

        Assert.Null(ctx.Result); // not short-circuited → allowed
    }

    [Fact]
    public async Task RequirePro_BlocksFreeUser_EvenWithForgedProClaim()
    {
        // DB says free; the JWT claim says pro. The guard must re-read the DB and block.
        var userId = await SeedUserAsync(tier: "free");
        var db = _fixture.GetDbContext();
        var filter = new RequireProAuthorizationFilter(db, NullLogger<RequireProAuthorizationFilter>.Instance);

        var ctx = BuildAuthContext(AuthenticatedPrincipal(userId, tierClaim: "pro"));
        await filter.OnAuthorizationAsync(ctx);

        var result = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task RequirePro_RejectsUnauthenticated()
    {
        var db = _fixture.GetDbContext();
        var filter = new RequireProAuthorizationFilter(db, NullLogger<RequireProAuthorizationFilter>.Instance);

        var ctx = BuildAuthContext(new ClaimsPrincipal(new ClaimsIdentity())); // no authenticationType
        await filter.OnAuthorizationAsync(ctx);

        Assert.IsType<UnauthorizedObjectResult>(ctx.Result);
    }
}
