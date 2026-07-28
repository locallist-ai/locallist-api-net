using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;

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
/// Aggregation runs IN MEMORY over a minimal projection (mirrors the Stats endpoints of
/// <c>AdminChatTurnsController</c> / <c>AdminPlanMetricsController</c>): billing-event volume is
/// tiny relative to chat turns, and it sidesteps SQL translation of the epoch-millis → day bucket.
/// </summary>
[ApiController]
[Route("admin/billing/metrics")]
[AdminAuthorize]
[EnableRateLimiting("AdminLimit")]
public class AdminBillingMetricsController : ControllerBase
{
    // Well-known RevenueCat webhook event types, stored verbatim in billing_events.event_type by
    // BillingEventProcessor (evt.Type). Matched case-insensitively for robustness.
    private const string InitialPurchase = "INITIAL_PURCHASE";
    private const string Renewal = "RENEWAL";
    private const string Cancellation = "CANCELLATION";
    private const string Uncancellation = "UNCANCELLATION";
    private const string Expiration = "EXPIRATION";
    private const string BillingIssue = "BILLING_ISSUE";
    private const string ProductChange = "PRODUCT_CHANGE";
    private const string Transfer = "TRANSFER";

    private readonly LocalListDbContext _db;
    private readonly ILogger<AdminBillingMetricsController> _logger;

    public AdminBillingMetricsController(LocalListDbContext db, ILogger<AdminBillingMetricsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Business KPIs over the billing-events ledger, optionally scoped to a range.
    /// The range filters on <c>event_timestamp_ms</c> (the authoritative RevenueCat event time —
    /// when the billing event actually happened, not when we ingested it). Omit <c>from</c> = all-time.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Metrics(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var query = _db.BillingEvents.AsNoTracking();

        if (from.HasValue)
        {
            var fromMs = from.Value.ToUnixTimeMilliseconds();
            query = query.Where(e => e.EventTimestampMs >= fromMs);
        }
        if (to.HasValue)
        {
            var toMs = to.Value.ToUnixTimeMilliseconds();
            query = query.Where(e => e.EventTimestampMs <= toMs);
        }

        var rows = await query
            .Select(e => new { e.EventType, e.EventTimestampMs, e.UserId })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return Ok(new AdminBillingMetricsDto(
                TotalEvents: 0,
                NewSubscriptions: 0, Renewals: 0, Cancellations: 0, Uncancellations: 0,
                Expirations: 0, BillingIssues: 0, ProductChanges: 0, Transfers: 0,
                UnresolvedEvents: 0, UniqueUsers: 0,
                ByEventType: new Dictionary<string, int>(),
                Daily: new List<AdminBillingDailyPointDto>()));
        }

        int CountType(string type) =>
            rows.Count(r => string.Equals(r.EventType, type, StringComparison.OrdinalIgnoreCase));

        var byEventType = rows
            .GroupBy(r => r.EventType)
            .ToDictionary(g => g.Key, g => g.Count());

        var daily = rows
            .GroupBy(r => DateOnly.FromDateTime(
                DateTimeOffset.FromUnixTimeMilliseconds(r.EventTimestampMs).UtcDateTime))
            .Select(g => new AdminBillingDailyPointDto(g.Key, g.Count()))
            .OrderBy(p => p.Date)
            .ToList();

        return Ok(new AdminBillingMetricsDto(
            TotalEvents: rows.Count,
            NewSubscriptions: CountType(InitialPurchase),
            Renewals: CountType(Renewal),
            Cancellations: CountType(Cancellation),
            Uncancellations: CountType(Uncancellation),
            Expirations: CountType(Expiration),
            BillingIssues: CountType(BillingIssue),
            ProductChanges: CountType(ProductChange),
            Transfers: CountType(Transfer),
            UnresolvedEvents: rows.Count(r => r.UserId == null),
            UniqueUsers: rows.Where(r => r.UserId != null).Select(r => r.UserId!.Value).Distinct().Count(),
            ByEventType: byEventType,
            Daily: daily));
    }
}
