using System.Net.Http.Headers;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Tests for GET /admin/billing/metrics — the read-only admin aggregation over billing_events.
/// Deterministic isolation: each aggregate test seeds into its OWN fixed far-past YEAR window and
/// asserts within it (windows never overlap, and no other test writes billing_events in those
/// years), so exact-equals assertions can't flake on a foreign row. billing_events.user_id has NO
/// foreign key (see the migration), so UserId is seeded with arbitrary guids.
/// </summary>
public class AdminBillingMetricsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const string ProductMonthly = "com.locallist.plus.monthly";
    private const string ProductYearly = "com.locallist.plus.yearly";

    // ── Aggregates + enriched segments (n = 9, exact values, deterministic) ───

    [Fact]
    public async Task Metrics_AggregatesAllSegments_WithExactValues()
    {
        // Fixed, test-private window (year 2006). day2 gives a 2-point time series.
        var day1 = new DateTimeOffset(2006, 3, 10, 10, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var userC = Guid.NewGuid();

        // day1 (5 events)
        await Seed("INITIAL_PURCHASE", userA, day1,
            product: ProductMonthly, period: "TRIAL", country: "US", price: 0m, currency: "USD", priceLocal: 0m, store: "APP_STORE");
        await Seed("INITIAL_PURCHASE", userB, day1,
            product: ProductYearly, period: "NORMAL", country: "US", price: 39.99m, currency: "USD", priceLocal: 39.99m, store: "APP_STORE");
        await Seed("INITIAL_PURCHASE", userC, day1,
            product: ProductMonthly, period: "TRIAL", country: "ES", price: 0m, currency: "EUR", priceLocal: 0m, store: "APP_STORE");
        await Seed("RENEWAL", userA, day1,
            product: ProductMonthly, period: "NORMAL", country: "US", price: 9.99m, currency: "USD", priceLocal: 9.99m, store: "APP_STORE", isTrialConversion: true);
        await Seed("CANCELLATION", userB, day1,
            product: ProductYearly, country: "US", cancelReason: "UNSUBSCRIBE");
        // day2 (4 events)
        await Seed("RENEWAL", userB, day2,
            product: ProductYearly, period: "NORMAL", country: "US", price: 39.99m, currency: "USD", priceLocal: 39.99m, store: "APP_STORE");
        await Seed("CANCELLATION", userC, day2,
            product: ProductMonthly, country: "ES", cancelReason: "CUSTOMER_SUPPORT");
        await Seed("EXPIRATION", userC, day2, product: ProductMonthly, country: "ES");
        await Seed("TRANSFER", null, day2); // unresolved (no mapped user, no product/country)

        var client = CreateAdminClient();
        var res = await client.GetAsync(
            $"/admin/billing/metrics?from={Enc(day1.AddDays(-1))}&to={Enc(day2.AddDays(1))}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var b = await res.Content.ReadFromJsonAsync<JsonElement>();

        // Scalar counts.
        Assert.Equal(9, b.GetProperty("totalEvents").GetInt32());
        Assert.Equal(3, b.GetProperty("newSubscriptions").GetInt32());
        Assert.Equal(2, b.GetProperty("trialStarts").GetInt32());          // A, C
        Assert.Equal(1, b.GetProperty("directPaidPurchases").GetInt32());  // B
        Assert.Equal(1, b.GetProperty("paidConversions").GetInt32());      // A's RENEWAL is_trial_conversion
        Assert.Equal(2, b.GetProperty("renewals").GetInt32());
        Assert.Equal(2, b.GetProperty("cancellations").GetInt32());
        Assert.Equal(1, b.GetProperty("expirations").GetInt32());
        Assert.Equal(1, b.GetProperty("transfers").GetInt32());
        Assert.Equal(1, b.GetProperty("unresolvedEvents").GetInt32());
        Assert.Equal(3, b.GetProperty("uniqueUsers").GetInt32());

        // Revenue: sum of USD price = 39.99 + 9.99 + 39.99 = 89.97 (trials/others are 0/null).
        Assert.Equal(89.97m, b.GetProperty("revenueUsd").GetDecimal());

        // Plan mix by product_id (TRANSFER has null product → excluded).
        var byProduct = b.GetProperty("byProductId");
        Assert.Equal(5, byProduct.GetProperty(ProductMonthly).GetInt32()); // 1,3,4,7,8
        Assert.Equal(3, byProduct.GetProperty(ProductYearly).GetInt32());  // 2,5,6

        // Country breakdown.
        var byCountry = b.GetProperty("byCountry");
        Assert.Equal(5, byCountry.GetProperty("US").GetInt32());
        Assert.Equal(3, byCountry.GetProperty("ES").GetInt32());

        // Refund vs voluntary churn (cancel_reason).
        var byCancel = b.GetProperty("byCancelReason");
        Assert.Equal(1, byCancel.GetProperty("UNSUBSCRIBE").GetInt32());
        Assert.Equal(1, byCancel.GetProperty("CUSTOMER_SUPPORT").GetInt32());

        // Localized revenue per currency (never summed across currencies).
        var byCur = b.GetProperty("revenueByCurrency");
        Assert.Equal(89.97m, byCur.GetProperty("USD").GetDecimal());
        Assert.Equal(0m, byCur.GetProperty("EUR").GetDecimal()); // only a 0-priced trial in EUR

        // Full event-type breakdown.
        var byType = b.GetProperty("byEventType");
        Assert.Equal(3, byType.GetProperty("INITIAL_PURCHASE").GetInt32());
        Assert.Equal(2, byType.GetProperty("RENEWAL").GetInt32());
        Assert.Equal(2, byType.GetProperty("CANCELLATION").GetInt32());

        // Daily time series: 2 UTC days, 5 then 4.
        var daily = b.GetProperty("daily").EnumerateArray().ToList();
        Assert.Equal(2, daily.Count);
        Assert.Equal(9, daily.Sum(d => d.GetProperty("count").GetInt32()));
        Assert.Equal("2006-03-10", daily[0].GetProperty("date").GetString());
        Assert.Equal(5, daily[0].GetProperty("count").GetInt32());
        Assert.Equal("2006-03-11", daily[1].GetProperty("date").GetString());
        Assert.Equal(4, daily[1].GetProperty("count").GetInt32());
    }

    // ── Empty table → zeros + 200 ─────────────────────────────────────────────

    [Fact]
    public async Task Metrics_Empty_ReturnsZeroes()
    {
        var from = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var client = CreateAdminClient();
        var res = await client.GetAsync($"/admin/billing/metrics?from={Enc(from)}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var b = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, b.GetProperty("totalEvents").GetInt32());
        Assert.Equal(0, b.GetProperty("newSubscriptions").GetInt32());
        Assert.Equal(0, b.GetProperty("trialStarts").GetInt32());
        Assert.Equal(0, b.GetProperty("paidConversions").GetInt32());
        Assert.Equal(0m, b.GetProperty("revenueUsd").GetDecimal());
        Assert.Equal(0, b.GetProperty("uniqueUsers").GetInt32());
        Assert.Empty(b.GetProperty("byEventType").EnumerateObject().ToList());
        Assert.Empty(b.GetProperty("byProductId").EnumerateObject().ToList());
        Assert.Empty(b.GetProperty("byCountry").EnumerateObject().ToList());
        Assert.Empty(b.GetProperty("byCancelReason").EnumerateObject().ToList());
        Assert.Empty(b.GetProperty("revenueByCurrency").EnumerateObject().ToList());
        Assert.Empty(b.GetProperty("daily").EnumerateArray().ToList());
    }

    // ── Range filter excludes rows outside [from,to] ──────────────────────────

    [Fact]
    public async Task Metrics_RangeFilter_ExcludesRowsOutsideWindow()
    {
        var inside = new DateTimeOffset(2009, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var user = Guid.NewGuid();

        await Seed("INITIAL_PURCHASE", user, inside, product: ProductMonthly, period: "NORMAL", country: "US", price: 9.99m, currency: "USD", priceLocal: 9.99m);
        await Seed("RENEWAL", user, inside.AddDays(-30));     // excluded
        await Seed("CANCELLATION", user, inside.AddDays(30)); // excluded

        var client = CreateAdminClient();
        var res = await client.GetAsync(
            $"/admin/billing/metrics?from={Enc(inside.AddDays(-1))}&to={Enc(inside.AddDays(1))}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var b = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, b.GetProperty("totalEvents").GetInt32());
        Assert.Equal(1, b.GetProperty("newSubscriptions").GetInt32());
        Assert.Equal(0, b.GetProperty("renewals").GetInt32());       // before-window excluded
        Assert.Equal(0, b.GetProperty("cancellations").GetInt32());  // after-window excluded
        Assert.Equal(9.99m, b.GetProperty("revenueUsd").GetDecimal());
    }

    // ── Auth: anonymous rejected (401) ────────────────────────────────────────

    [Fact]
    public async Task Metrics_Anonymous_Rejected()
    {
        var client = fixture.CreateClient();
        var res = await client.GetAsync("/admin/billing/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ── Auth: app HS256 (AppScheme) token rejected at the admin gate (403) ────

    [Fact]
    public async Task Metrics_AppSchemeToken_ForbiddenNot401()
    {
        // A valid app-scheme (HS256) token AUTHENTICATES as a normal user; the FirebaseScheme admin
        // gate then rejects it with 403 (authenticated, but not an admin caller). Pinned to exactly
        // Forbidden: a 401 here would mean AppScheme stopped authenticating — a regression this test
        // must catch, not hide. Proves the endpoint is behind the admin RS256 gate, not app HS256.
        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            userId, $"app-billing-{userId:N}@test.com");

        var res = await client.GetAsync("/admin/billing/metrics");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task Seed(
        string eventType, Guid? userId, DateTimeOffset when,
        string? product = null, string? period = null, string? country = null,
        decimal? price = null, string? currency = null, decimal? priceLocal = null,
        string? store = null, string? cancelReason = null, bool? isTrialConversion = null)
    {
        var db = fixture.GetDbContext();
        db.BillingEvents.Add(new BillingEvent
        {
            Id = Guid.NewGuid(),
            RcEventId = $"evt-{Guid.NewGuid():N}", // UNIQUE index — one per row
            UserId = userId,
            AppUserId = userId?.ToString(),
            EventType = eventType,
            EventTimestampMs = when.ToUnixTimeMilliseconds(),
            ProcessedAt = DateTimeOffset.UtcNow,
            ProductId = product,
            PeriodType = period,
            CountryCode = country,
            Price = price,
            Currency = currency,
            PriceInPurchasedCurrency = priceLocal,
            Store = store,
            CancelReason = cancelReason,
            IsTrialConversion = isTrialConversion,
        });
        await db.SaveChangesAsync(); // per-row SaveChanges as instructed
    }

    private static string Enc(DateTimeOffset t) => Uri.EscapeDataString(t.ToString("o"));

    private HttpClient CreateAdminClient()
    {
        // The admin gate is 100% token-claim based (IsAdminCaller: Firebase RS256 issuer +
        // @locallist.ai email + email_verified). NO DB user row is consulted — so we deliberately
        // do NOT seed a User{Role="admin"} here; the Role column grants nothing on this path.
        var adminEmail = $"admin-billing-{Guid.NewGuid():N}@locallist.ai";
        var adminFbUid = $"fb-admin-billing-{Guid.NewGuid():N}";

        var client = fixture.CreateClient();
        var token = fixture.CreateToken(adminFbUid, adminEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
