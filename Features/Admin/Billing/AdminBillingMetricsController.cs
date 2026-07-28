using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Admin.Billing;

/// <summary>
/// Read-only admin dashboard aggregation over the <c>billing_events</c> ledger.
///
/// Auth: <see cref="AdminAuthorizeAttribute"/> — same FirebaseScheme (RS256) admin gate as the
/// other <c>Admin*Controller</c>s. An anonymous caller → 401; an authenticated but non-admin
/// caller (e.g. an app HS256 token) → 403. NOT the app HS256 scheme.
///
/// Empty-safe by construction: the table is EMPTY until IAP goes live, so zero rows → zeroed
/// metrics + empty collections + HTTP 200, never an error.
///
/// PERFORMANCE: every aggregate is computed IN THE DATABASE (GROUP BY / COUNT / SUM); the ledger
/// is never materialized into memory. The range predicate hits <c>event_timestamp_ms</c>, which is
/// index-backed by <c>IX_billing_events_event_timestamp_ms</c>. The common pre-IAP empty case
/// short-circuits after a single COUNT-shaped query.
/// </summary>
[ApiController]
[Route("admin/billing/metrics")]
[AdminAuthorize]
[EnableRateLimiting("AdminLimit")]
public class AdminBillingMetricsController : ControllerBase
{
    // Well-known RevenueCat webhook event types, stored verbatim in billing_events.event_type by
    // BillingEventProcessor (evt.Type). RevenueCat always delivers them uppercase.
    private const string InitialPurchase = "INITIAL_PURCHASE";
    private const string Renewal = "RENEWAL";
    private const string Cancellation = "CANCELLATION";
    private const string Uncancellation = "UNCANCELLATION";
    private const string Expiration = "EXPIRATION";
    private const string BillingIssue = "BILLING_ISSUE";
    private const string ProductChange = "PRODUCT_CHANGE";
    private const string Transfer = "TRANSFER";

    private const string TrialPeriod = "TRIAL";
    private const long MsPerDay = 86_400_000L;

    private readonly LocalListDbContext _db;
    private readonly ILogger<AdminBillingMetricsController> _logger;

    public AdminBillingMetricsController(LocalListDbContext db, ILogger<AdminBillingMetricsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Business KPIs over the billing-events ledger, optionally scoped to a range.
    /// The range filters on <c>event_timestamp_ms</c> (when the billing event actually happened at
    /// RevenueCat, not when we ingested it). Omit <c>from</c> = all-time.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Metrics(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        IQueryable<BillingEvent> baseQuery = _db.BillingEvents.AsNoTracking();

        if (from.HasValue)
        {
            var fromMs = from.Value.ToUnixTimeMilliseconds();
            baseQuery = baseQuery.Where(e => e.EventTimestampMs >= fromMs);
        }
        if (to.HasValue)
        {
            var toMs = to.Value.ToUnixTimeMilliseconds();
            baseQuery = baseQuery.Where(e => e.EventTimestampMs <= toMs);
        }

        // (1) Full breakdown by event type — SQL GROUP BY. Also the source of TotalEvents and the
        //     named per-type counts. Tiny result set (one row per distinct type).
        var byEventTypeRows = await baseQuery
            .GroupBy(e => e.EventType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var totalEvents = byEventTypeRows.Sum(r => r.Count);

        // Empty (the pre-IAP norm): short-circuit with a zeroed DTO before issuing more queries.
        if (totalEvents == 0)
            return Ok(Empty());

        int CountType(string type) =>
            byEventTypeRows
                .Where(r => string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase))
                .Sum(r => r.Count);

        var newSubscriptions = CountType(InitialPurchase);

        // (2) Trial vs direct-paid split of new subscriptions — SQL COUNT with predicate.
        var trialStarts = await baseQuery
            .CountAsync(e => e.EventType == InitialPurchase && e.PeriodType == TrialPeriod, ct);
        var directPaidPurchases = newSubscriptions - trialStarts;

        // (3) Paid conversions — the clean in-payload signal (RENEWAL flagged is_trial_conversion).
        var paidConversions = await baseQuery.CountAsync(e => e.IsTrialConversion == true, ct);

        // (4) User attribution — SQL COUNT / COUNT(DISTINCT).
        var unresolvedEvents = await baseQuery.CountAsync(e => e.UserId == null, ct);
        var uniqueUsers = await baseQuery
            .Where(e => e.UserId != null)
            .Select(e => e.UserId)
            .Distinct()
            .CountAsync(ct);

        // (5) Revenue — SUM in SQL. price is RC's USD-normalized amount, so it sums cleanly;
        //     nullable so an empty/all-null slice yields null → 0.
        var revenueUsd = (await baseQuery.SumAsync(e => e.Price, ct)) ?? 0m;

        // (6) Segment breakdowns — SQL GROUP BY over the analytics columns (null keys filtered out).
        var byProductId = await GroupCountAsync(baseQuery, e => e.ProductId, ct);
        var byCountry = await GroupCountAsync(baseQuery, e => e.CountryCode, ct);
        var byCancelReason = await GroupCountAsync(baseQuery, e => e.CancelReason, ct);

        var revenueByCurrency = (await baseQuery
                .Where(e => e.Currency != null)
                .GroupBy(e => e.Currency!)
                .Select(g => new { Currency = g.Key, Sum = g.Sum(x => x.PriceInPurchasedCurrency) })
                .ToListAsync(ct))
            .ToDictionary(x => x.Currency, x => x.Sum ?? 0m);

        // (7) Daily time series — bucket epoch-millis into UTC days IN SQL via integer division
        //     (event_timestamp_ms is always ≥ 0, so bigint division truncates to the day number).
        var dailyRows = await baseQuery
            .GroupBy(e => e.EventTimestampMs / MsPerDay)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .OrderBy(x => x.Day)
            .ToListAsync(ct);

        var daily = dailyRows
            .Select(r => new AdminBillingDailyPointDto(
                DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(r.Day * MsPerDay).UtcDateTime),
                r.Count))
            .ToList();

        return Ok(new AdminBillingMetricsDto(
            TotalEvents: totalEvents,
            NewSubscriptions: newSubscriptions,
            TrialStarts: trialStarts,
            DirectPaidPurchases: directPaidPurchases,
            PaidConversions: paidConversions,
            Renewals: CountType(Renewal),
            Cancellations: CountType(Cancellation),
            Uncancellations: CountType(Uncancellation),
            Expirations: CountType(Expiration),
            BillingIssues: CountType(BillingIssue),
            ProductChanges: CountType(ProductChange),
            Transfers: CountType(Transfer),
            UnresolvedEvents: unresolvedEvents,
            UniqueUsers: uniqueUsers,
            RevenueUsd: revenueUsd,
            ByEventType: byEventTypeRows.ToDictionary(r => r.Type, r => r.Count),
            ByProductId: byProductId,
            ByCountry: byCountry,
            ByCancelReason: byCancelReason,
            RevenueByCurrency: revenueByCurrency,
            Daily: daily));
    }

    /// <summary>
    /// SQL GROUP BY over a nullable string column → {value: count}, null keys excluded. Runs
    /// entirely in the database (the projection + GROUP BY translate; nothing is materialized first).
    /// </summary>
    private static async Task<Dictionary<string, int>> GroupCountAsync(
        IQueryable<BillingEvent> query,
        System.Linq.Expressions.Expression<Func<BillingEvent, string?>> selector,
        CancellationToken ct)
    {
        var rows = await query
            .Select(selector)
            .Where(v => v != null)
            .GroupBy(v => v!)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => r.Count);
    }

    private static AdminBillingMetricsDto Empty() => new(
        TotalEvents: 0,
        NewSubscriptions: 0, TrialStarts: 0, DirectPaidPurchases: 0, PaidConversions: 0,
        Renewals: 0, Cancellations: 0, Uncancellations: 0, Expirations: 0,
        BillingIssues: 0, ProductChanges: 0, Transfers: 0,
        UnresolvedEvents: 0, UniqueUsers: 0, RevenueUsd: 0m,
        ByEventType: new Dictionary<string, int>(),
        ByProductId: new Dictionary<string, int>(),
        ByCountry: new Dictionary<string, int>(),
        ByCancelReason: new Dictionary<string, int>(),
        RevenueByCurrency: new Dictionary<string, decimal>(),
        Daily: new List<AdminBillingDailyPointDto>());
}
