using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.NET.Shared.Data.Entities;

/// <summary>
/// Ledger of processed RevenueCat webhook events. Two jobs:
///  1. Idempotency — <see cref="RcEventId"/> has a UNIQUE index, so a duplicate
///     delivery (RevenueCat retries + at-least-once semantics) is rejected at the DB.
///  2. Reorder safety — <see cref="EventTimestampMs"/> lets the processor ignore a
///     stale event that arrives after a newer one already moved the user's tier.
/// This table is append-only; it is never read on the hot path of gated endpoints.
/// </summary>
[Table("billing_events")]
public class BillingEvent
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>RevenueCat's <c>event.id</c> — globally unique per event, used for dedup.</summary>
    [Column("rc_event_id")]
    [StringLength(255)]
    [Required]
    public string RcEventId { get; set; } = string.Empty;

    /// <summary>Resolved LocalList user, or null when the app_user_id could not be mapped.</summary>
    [Column("user_id")]
    public Guid? UserId { get; set; }

    /// <summary>Raw RevenueCat <c>app_user_id</c> as received (kept for audit / unresolved events).</summary>
    [Column("app_user_id")]
    [StringLength(255)]
    public string? AppUserId { get; set; }

    [Column("event_type")]
    [StringLength(64)]
    [Required]
    public string EventType { get; set; } = string.Empty;

    /// <summary>RevenueCat <c>event_timestamp_ms</c> — source-of-truth ordering key.</summary>
    [Column("event_timestamp_ms")]
    public long EventTimestampMs { get; set; }

    [Column("processed_at")]
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;

    // ── Analytics-only columns (added 2026-07-28) ────────────────────────────
    // Populated best-effort from the RevenueCat webhook event for the admin dashboard
    // (plan mix / country / trial-vs-paid / conversion / revenue). ALL nullable — pre-IAP
    // and pre-migration rows stay null, and any field absent from a given event stays null.
    // NONE of these drive tier: the tier is derived from RevenueCat's REST state, never from
    // the payload (see BillingEventProcessor). These are write-only side data for reporting.

    /// <summary>RevenueCat <c>product_id</c> — the purchased product identifier (plan mix).</summary>
    [Column("product_id")]
    [StringLength(255)]
    public string? ProductId { get; set; }

    /// <summary>RevenueCat <c>period_type</c> — TRIAL | INTRO | NORMAL | PROMOTIONAL | PREPAID.
    /// A trial start is an INITIAL_PURCHASE with period_type == TRIAL.</summary>
    [Column("period_type")]
    [StringLength(32)]
    public string? PeriodType { get; set; }

    /// <summary>RevenueCat <c>country_code</c> — ISO 3166-1 alpha-2 buyer country.</summary>
    [Column("country_code")]
    [StringLength(8)]
    public string? CountryCode { get; set; }

    /// <summary>RevenueCat <c>price</c> — the transaction price normalized to USD by RevenueCat.
    /// Trials/non-transactions carry 0/null, so summing this yields real USD revenue.</summary>
    [Column("price")]
    [Precision(12, 4)]
    public decimal? Price { get; set; }

    /// <summary>RevenueCat <c>price_in_purchased_currency</c> — the price in the buyer's currency.</summary>
    [Column("price_in_purchased_currency")]
    [Precision(12, 4)]
    public decimal? PriceInPurchasedCurrency { get; set; }

    /// <summary>RevenueCat <c>currency</c> — ISO 4217 code of <see cref="PriceInPurchasedCurrency"/>.</summary>
    [Column("currency")]
    [StringLength(8)]
    public string? Currency { get; set; }

    /// <summary>RevenueCat <c>store</c> — APP_STORE | PLAY_STORE | STRIPE | RC_BILLING | ...</summary>
    [Column("store")]
    [StringLength(32)]
    public string? Store { get; set; }

    /// <summary>RevenueCat <c>cancel_reason</c> on CANCELLATION events — UNSUBSCRIBE |
    /// BILLING_ERROR | DEVELOPER_INITIATED | PRICE_INCREASE | CUSTOMER_SUPPORT | UNKNOWN.
    /// Lets the dashboard separate voluntary churn / refunds from billing failures.</summary>
    [Column("cancel_reason")]
    [StringLength(64)]
    public string? CancelReason { get; set; }

    /// <summary>RevenueCat <c>is_trial_conversion</c> — true on a RENEWAL that is a trial→paid
    /// conversion. The one clean in-payload signal for a paid conversion (no cross-event
    /// correlation needed). Absent on most events → null.</summary>
    [Column("is_trial_conversion")]
    public bool? IsTrialConversion { get; set; }
}
