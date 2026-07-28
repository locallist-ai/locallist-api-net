using System.Net.Http.Headers;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Tests for GET /admin/billing/metrics — the read-only admin aggregation over billing_events.
/// Each aggregate test scopes its assertions to a UNIQUE from/to window so that rows inserted by
/// other tests (e.g. the webhook tests) against the shared Postgres container cannot pollute the
/// counts. billing_events.user_id has NO foreign key (see the migration), so UserId can be seeded
/// with arbitrary guids without materialising real users.
/// </summary>
public class AdminBillingMetricsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // ── Aggregates + breakdown (n = 9, non-vacuous: asserts actual values) ────

    [Fact]
    public async Task Metrics_AggregatesEventTypes_AndSegments()
    {
        // A unique far-past window isolates these 9 seeds from any webhook-inserted rows (~now).
        var day1 = new DateTimeOffset(2006, 3, 1, 0, 0, 0, TimeSpan.Zero)
            .AddDays(Random.Shared.Next(0, 3000)).AddHours(10);
        var day2 = day1.AddDays(1); // a second UTC day for the time series

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var userC = Guid.NewGuid();

        // day1: 5 events
        await SeedEvent("INITIAL_PURCHASE", userA, day1);
        await SeedEvent("INITIAL_PURCHASE", userB, day1);
        await SeedEvent("RENEWAL", userA, day1);
        await SeedEvent("CANCELLATION", userA, day1);
        await SeedEvent("TRANSFER", null, day1); // unresolved (no mapped user)
        // day2: 4 events
        await SeedEvent("RENEWAL", userB, day2);
        await SeedEvent("EXPIRATION", userC, day2);
        await SeedEvent("PRODUCT_CHANGE", userB, day2);
        await SeedEvent("UNCANCELLATION", userC, day2);

        var from = day1.AddDays(-1);
        var to = day2.AddDays(1);
        var client = CreateAdminClient();
        var res = await client.GetAsync(
            $"/admin/billing/metrics?from={Enc(from)}&to={Enc(to)}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(9, body.GetProperty("totalEvents").GetInt32());
        Assert.Equal(2, body.GetProperty("newSubscriptions").GetInt32());   // 2 INITIAL_PURCHASE
        Assert.Equal(2, body.GetProperty("renewals").GetInt32());            // 2 RENEWAL
        Assert.Equal(1, body.GetProperty("cancellations").GetInt32());       // 1 CANCELLATION
        Assert.Equal(1, body.GetProperty("uncancellations").GetInt32());     // 1 UNCANCELLATION
        Assert.Equal(1, body.GetProperty("expirations").GetInt32());         // 1 EXPIRATION
        Assert.Equal(1, body.GetProperty("productChanges").GetInt32());      // 1 PRODUCT_CHANGE
        Assert.Equal(1, body.GetProperty("transfers").GetInt32());           // 1 TRANSFER
        Assert.Equal(1, body.GetProperty("unresolvedEvents").GetInt32());    // the null-user TRANSFER
        Assert.Equal(3, body.GetProperty("uniqueUsers").GetInt32());         // A, B, C

        // Authoritative full breakdown by event_type.
        var byType = body.GetProperty("byEventType");
        Assert.Equal(2, byType.GetProperty("INITIAL_PURCHASE").GetInt32());
        Assert.Equal(2, byType.GetProperty("RENEWAL").GetInt32());
        Assert.Equal(1, byType.GetProperty("CANCELLATION").GetInt32());
        Assert.Equal(1, byType.GetProperty("PRODUCT_CHANGE").GetInt32());

        // Time series: two UTC days, summing to the total.
        var daily = body.GetProperty("daily").EnumerateArray().ToList();
        Assert.Equal(2, daily.Count);
        Assert.Equal(9, daily.Sum(d => d.GetProperty("count").GetInt32()));
        Assert.Equal(5, daily[0].GetProperty("count").GetInt32()); // day1 (ascending order)
        Assert.Equal(4, daily[1].GetProperty("count").GetInt32()); // day2
        Assert.Equal(
            DateOnly.FromDateTime(day1.UtcDateTime).ToString("yyyy-MM-dd"),
            daily[0].GetProperty("date").GetString());
    }

    // ── Empty table → zeros + 200 ─────────────────────────────────────────────

    [Fact]
    public async Task Metrics_Empty_ReturnsZeroes()
    {
        // A future window is guaranteed to contain no billing events.
        var from = DateTimeOffset.UtcNow.AddYears(20);
        var client = CreateAdminClient();
        var res = await client.GetAsync($"/admin/billing/metrics?from={Enc(from)}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("totalEvents").GetInt32());
        Assert.Equal(0, body.GetProperty("newSubscriptions").GetInt32());
        Assert.Equal(0, body.GetProperty("renewals").GetInt32());
        Assert.Equal(0, body.GetProperty("cancellations").GetInt32());
        Assert.Equal(0, body.GetProperty("unresolvedEvents").GetInt32());
        Assert.Equal(0, body.GetProperty("uniqueUsers").GetInt32());
        Assert.Empty(body.GetProperty("byEventType").EnumerateObject().ToList());
        Assert.Empty(body.GetProperty("daily").EnumerateArray().ToList());
    }

    // ── Range filter excludes rows outside [from,to] ──────────────────────────

    [Fact]
    public async Task Metrics_RangeFilter_ExcludesRowsOutsideWindow()
    {
        var inside = new DateTimeOffset(2007, 6, 1, 12, 0, 0, TimeSpan.Zero)
            .AddDays(Random.Shared.Next(0, 3000));
        var beforeWindow = inside.AddDays(-30);
        var afterWindow = inside.AddDays(30);
        var user = Guid.NewGuid();

        await SeedEvent("INITIAL_PURCHASE", user, inside);
        await SeedEvent("RENEWAL", user, beforeWindow); // must be excluded
        await SeedEvent("CANCELLATION", user, afterWindow); // must be excluded

        var from = inside.AddDays(-1);
        var to = inside.AddDays(1);
        var client = CreateAdminClient();
        var res = await client.GetAsync(
            $"/admin/billing/metrics?from={Enc(from)}&to={Enc(to)}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("totalEvents").GetInt32());
        Assert.Equal(1, body.GetProperty("newSubscriptions").GetInt32());
        Assert.Equal(0, body.GetProperty("renewals").GetInt32());       // before-window excluded
        Assert.Equal(0, body.GetProperty("cancellations").GetInt32());  // after-window excluded
    }

    // ── Auth: anonymous rejected ──────────────────────────────────────────────

    [Fact]
    public async Task Metrics_Anonymous_Rejected()
    {
        var client = fixture.CreateClient();
        var res = await client.GetAsync("/admin/billing/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ── Auth: app HS256 (AppScheme) token rejected — NOT the admin scheme ─────

    [Fact]
    public async Task Metrics_AppSchemeToken_Rejected()
    {
        // A valid app-scheme (HS256) token authenticates as a normal user but is NOT an admin
        // caller, so the FirebaseScheme admin gate rejects it (authenticated → 403 Forbidden).
        // This proves the endpoint is behind the admin RS256 gate, not the app HS256 scheme.
        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            userId, $"app-billing-{userId:N}@test.com");

        var res = await client.GetAsync("/admin/billing/metrics");

        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        Assert.True(
            res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"expected app token to be rejected, got {(int)res.StatusCode} {res.StatusCode}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task SeedEvent(string eventType, Guid? userId, DateTimeOffset when)
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
        });
        await db.SaveChangesAsync(); // per-row SaveChanges as instructed
    }

    private static string Enc(DateTimeOffset t) => Uri.EscapeDataString(t.ToString("o"));

    private HttpClient CreateAdminClient()
    {
        var adminEmail = $"admin-billing-{Guid.NewGuid():N}@locallist.ai";
        var adminFbUid = $"fb-admin-billing-{Guid.NewGuid():N}";

        var db = fixture.GetDbContext();
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = adminEmail,
            FirebaseUid = adminFbUid,
            Role = "admin"
        });
        db.SaveChanges();

        var client = fixture.CreateClient();
        var token = fixture.CreateToken(adminFbUid, adminEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
