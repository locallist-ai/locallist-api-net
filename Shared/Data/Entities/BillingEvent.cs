using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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
}
