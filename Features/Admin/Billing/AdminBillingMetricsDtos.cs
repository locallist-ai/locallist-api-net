namespace LocalList.API.NET.Features.Admin.Billing;

/// <summary>
/// Aggregated business KPIs over the <c>billing_events</c> ledger, for the admin dashboard.
/// Empty-safe: zero rows in range → all counts 0, dictionary/list empty, HTTP 200.
///
/// SCHEMA NOTE (honest gap list): <c>billing_events</c> persists ONLY the event type, the two
/// timestamps and the resolved local user (see <see cref="Shared.Data.Entities.BillingEvent"/>).
/// It does NOT store <c>product_id</c>, price/currency, <c>country_code</c> or <c>period_type</c>.
/// Therefore the following segments Pablo asked for are NOT derivable from this table and are
/// deliberately OMITTED rather than fabricated:
///   • plan mix (monthly vs yearly)      → needs product_id / period_type
///   • revenue / MRR                     → needs price + currency
///   • breakdown by country              → needs country_code
///   • trial-vs-paid split, conversions  → needs period_type (a trial start is an
///                                          INITIAL_PURCHASE with period_type=TRIAL)
///   • refunds vs plain cancellations    → needs cancel_reason (both arrive as CANCELLATION)
/// Everything below is derived from <c>event_type</c> alone (+ user_id / event_timestamp_ms).
/// </summary>
public record AdminBillingMetricsDto(
    int TotalEvents,
    // INITIAL_PURCHASE. New subscriptions. NB: trial-start vs direct-paid is NOT separable here
    // (no period_type column) — see the schema note above.
    int NewSubscriptions,
    // RENEWAL — active / renewed subscriptions.
    int Renewals,
    // CANCELLATION — auto-renew turned off. NB: refunds also arrive as CANCELLATION and are NOT
    // separable here (no cancel_reason column).
    int Cancellations,
    // UNCANCELLATION — auto-renew re-enabled.
    int Uncancellations,
    // EXPIRATION — subscription lapsed.
    int Expirations,
    // BILLING_ISSUE — a renewal failed to charge.
    int BillingIssues,
    // PRODUCT_CHANGE — up/downgrade or plan switch.
    int ProductChanges,
    // TRANSFER — subscription moved between App User IDs.
    int Transfers,
    // Events whose app_user_id did not map to any LocalList user (user_id IS NULL).
    int UnresolvedEvents,
    // Distinct mapped users touched by a billing event in range.
    int UniqueUsers,
    // Authoritative full breakdown by event_type (stored verbatim) so nothing is lost even for
    // event types not surfaced as a named field above.
    IReadOnlyDictionary<string, int> ByEventType,
    // Events per UTC day over the range (by event_timestamp_ms), ascending. Small + cheap.
    IReadOnlyList<AdminBillingDailyPointDto> Daily);

/// <summary>One point of the daily time series: a UTC calendar day and the event count on it.</summary>
public record AdminBillingDailyPointDto(DateOnly Date, int Count);
