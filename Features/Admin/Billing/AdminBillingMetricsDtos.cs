namespace LocalList.API.NET.Features.Admin.Billing;

/// <summary>
/// Aggregated business KPIs over the <c>billing_events</c> ledger, for the admin dashboard.
/// Empty-safe: zero rows in range → all counts 0, dictionaries/list empty, HTTP 200.
///
/// All aggregation runs in SQL (GROUP BY / COUNT / SUM) — the endpoint never streams the ledger
/// into memory. The date range filters on <c>event_timestamp_ms</c> (the authoritative RevenueCat
/// event time), which is index-backed (<c>IX_billing_events_event_timestamp_ms</c>).
///
/// SEGMENTS THAT ARE REAL (from the analytics columns captured on the row):
///   • plan mix           → <see cref="ByProductId"/> (product_id). Monthly-vs-yearly is NOT a
///                          webhook field, so the caller maps product IDs to durations.
///   • country breakdown  → <see cref="ByCountry"/> (country_code).
///   • trial vs paid      → <see cref="TrialStarts"/> / <see cref="DirectPaidPurchases"/> split of
///                          new subscriptions by period_type == TRIAL.
///   • paid conversions   → <see cref="PaidConversions"/> = RENEWALs flagged is_trial_conversion.
///                          Exposed as a COUNT, not a rate: it is the clean in-payload signal, but a
///                          precise trial→paid *rate* would need cross-event/cohort correlation
///                          (which trial started, when) that a single-table query can't do honestly.
///   • refunds vs churn   → <see cref="ByCancelReason"/> (cancel_reason on CANCELLATION events).
///   • revenue            → <see cref="RevenueUsd"/> (sum of RC-normalized USD <c>price</c>) plus
///                          <see cref="RevenueByCurrency"/> (localized, kept PER CURRENCY — never
///                          summed across currencies).
///
/// STILL NOT DERIVABLE here: exact MRR/ARR (needs plan-duration mapping + active-subscriber state,
/// not raw events) and net revenue after refunds (refund amounts aren't a distinct webhook field).
/// </summary>
public record AdminBillingMetricsDto(
    int TotalEvents,
    // INITIAL_PURCHASE total (= TrialStarts + DirectPaidPurchases).
    int NewSubscriptions,
    // INITIAL_PURCHASE with period_type == TRIAL.
    int TrialStarts,
    // INITIAL_PURCHASE without a trial (period_type NORMAL/INTRO/PROMOTIONAL/null).
    int DirectPaidPurchases,
    // RENEWAL events flagged is_trial_conversion == true (a trial that converted to paid).
    int PaidConversions,
    // RENEWAL — active / renewed subscriptions (includes conversions).
    int Renewals,
    // CANCELLATION — auto-renew turned off (see ByCancelReason to split refund vs voluntary churn).
    int Cancellations,
    int Uncancellations,   // UNCANCELLATION
    int Expirations,       // EXPIRATION
    int BillingIssues,     // BILLING_ISSUE
    int ProductChanges,    // PRODUCT_CHANGE
    int Transfers,         // TRANSFER
    int UnresolvedEvents,  // events with no mapped LocalList user (user_id IS NULL)
    int UniqueUsers,       // distinct mapped users touched in range
    decimal RevenueUsd,    // sum of RC price (USD-normalized); trials/non-transactions contribute 0
    IReadOnlyDictionary<string, int> ByEventType,            // authoritative full breakdown
    IReadOnlyDictionary<string, int> ByProductId,            // plan mix
    IReadOnlyDictionary<string, int> ByCountry,              // country breakdown
    IReadOnlyDictionary<string, int> ByCancelReason,         // refunds vs voluntary churn
    IReadOnlyDictionary<string, decimal> RevenueByCurrency,  // localized revenue, per currency
    IReadOnlyList<AdminBillingDailyPointDto> Daily);         // events per UTC day over the range

/// <summary>One point of the daily time series: a UTC calendar day and the event count on it.</summary>
public record AdminBillingDailyPointDto(DateOnly Date, int Count);
