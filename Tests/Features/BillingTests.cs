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
