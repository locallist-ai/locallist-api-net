using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.NET.Features.Billing;

/// <summary>Outcome of processing a single RevenueCat event — used only for logging/tests.</summary>
public enum BillingEventOutcome
{
    /// <summary>Event id already in the ledger — no-op (idempotent replay).</summary>
    Duplicate,
    /// <summary>Applied and set the user to the "pro" tier.</summary>
    GrantedPro,
    /// <summary>Applied and reverted the user to the "free" tier.</summary>
    RevokedToFree,
    /// <summary>Recorded but no tier change (e.g. CANCELLATION keeps access until EXPIRATION).</summary>
    NoTierChange,
    /// <summary>Recorded but skipped the tier change because a newer event already applied.</summary>
    StaleReorder,
    /// <summary>Recorded with a null user — the app_user_id did not map to any LocalList user.</summary>
    UserNotFound,
}

/// <summary>
/// Applies RevenueCat subscriber events to <see cref="User.Tier"/>. This is the ONLY
/// server-side writer of the tier column driven by billing. Every mutation is:
///   - idempotent (deduped by <see cref="BillingEvent.RcEventId"/> + a UNIQUE index race guard),
///   - reorder-safe (a stale event, by <c>event_timestamp_ms</c>, never clobbers newer state),
///   - transactional (ledger insert + tier write commit together).
/// Slice-local service (VSA): lives in Features/Billing, imports no other slice.
/// </summary>
public class BillingEventProcessor
{
    // Event types that grant "pro" when the plus entitlement is active.
    private static readonly HashSet<string> GrantTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "INITIAL_PURCHASE",
        "RENEWAL",
        "PRODUCT_CHANGE",
        "UNCANCELLATION",
        "SUBSCRIPTION_EXTENDED",
        "NON_RENEWING_PURCHASE",
    };

    // Event types that revoke access (back to "free").
    private static readonly HashSet<string> RevokeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "EXPIRATION",
        "SUBSCRIPTION_PAUSED",
    };

    private const string TierPro = "pro";
    private const string TierFree = "free";

    private readonly LocalListDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<BillingEventProcessor> _logger;

    public BillingEventProcessor(
        LocalListDbContext db, TimeProvider clock, ILogger<BillingEventProcessor> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Processes one event. Assumes the caller already verified the webhook authorization.
    /// Returns the outcome; never throws for business cases (only for infra failures).
    /// </summary>
    public async Task<BillingEventOutcome> ProcessAsync(
        RevenueCatEvent evt, string plusEntitlementId, CancellationToken ct)
    {
        var rcEventId = evt.Id!; // validated non-empty by the caller
        var appUserId = evt.AppUserId ?? evt.OriginalAppUserId;

        // Fast-path dedup: if we've already recorded this event id, do nothing.
        if (await _db.BillingEvents.AnyAsync(be => be.RcEventId == rcEventId, ct))
        {
            _logger.LogInformation("RevenueCat event {EventId} already processed; skipping", rcEventId);
            return BillingEventOutcome.Duplicate;
        }

        var user = await ResolveUserAsync(evt, ct);

        // Decide the tier effect from the event type + entitlement, honoring reorder.
        var outcome = BillingEventOutcome.NoTierChange;
        if (user is null)
        {
            outcome = BillingEventOutcome.UserNotFound;
            _logger.LogWarning(
                "RevenueCat event {EventId} ({Type}): no user for app_user_id {AppUserId}; recorded unresolved",
                rcEventId, evt.Type, appUserId);
        }
        else if (evt.AffectsEntitlement(plusEntitlementId))
        {
            var isNewest = await IsNewestForUserAsync(user.Id, evt.EventTimestampMs, ct);
            if (!isNewest)
            {
                outcome = BillingEventOutcome.StaleReorder;
                _logger.LogInformation(
                    "RevenueCat event {EventId} ({Type}) for user {UserId} is stale (ts {Ts}); tier unchanged",
                    rcEventId, evt.Type, user.Id, evt.EventTimestampMs);
            }
            else if (GrantTypes.Contains(evt.Type ?? string.Empty))
            {
                if (user.Tier != TierPro) user.Tier = TierPro;
                // Persist the RevenueCat customer link so /account and future events resolve.
                if (string.IsNullOrEmpty(user.RcCustomerId) && !string.IsNullOrEmpty(appUserId))
                    user.RcCustomerId = appUserId;
                user.UpdatedAt = _clock.GetUtcNow();
                outcome = BillingEventOutcome.GrantedPro;
            }
            else if (RevokeTypes.Contains(evt.Type ?? string.Empty))
            {
                if (user.Tier != TierFree) user.Tier = TierFree;
                user.UpdatedAt = _clock.GetUtcNow();
                outcome = BillingEventOutcome.RevokedToFree;
            }
            // CANCELLATION / BILLING_ISSUE / TRANSFER / TEST → recorded, no tier change.
        }

        _db.BillingEvents.Add(new BillingEvent
        {
            RcEventId = rcEventId,
            UserId = user?.Id,
            AppUserId = appUserId,
            EventType = evt.Type ?? "UNKNOWN",
            EventTimestampMs = evt.EventTimestampMs,
            ProcessedAt = _clock.GetUtcNow(),
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent delivery of the same event id won the INSERT race. The tier
            // write in THIS transaction is rolled back with it — the winner already
            // applied the identical effect. Treat as an idempotent duplicate.
            _logger.LogInformation(
                "RevenueCat event {EventId} lost the insert race (concurrent duplicate); treating as processed",
                rcEventId);
            return BillingEventOutcome.Duplicate;
        }

        _logger.LogInformation(
            "RevenueCat event {EventId} ({Type}) processed for user {UserId}: {Outcome}",
            rcEventId, evt.Type, user?.Id, outcome);
        return outcome;
    }

    /// <summary>
    /// Maps a RevenueCat app_user_id to a LocalList user. The app sets app_user_id to the
    /// User.Id (Guid); older/anonymous flows may land it in rc_customer_id. Tries both the
    /// primary and original identifiers.
    /// </summary>
    private async Task<User?> ResolveUserAsync(RevenueCatEvent evt, CancellationToken ct)
    {
        foreach (var candidate in new[] { evt.AppUserId, evt.OriginalAppUserId })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            if (Guid.TryParse(candidate, out var userId))
            {
                var byId = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
                if (byId is not null) return byId;
            }

            var byRc = await _db.Users.FirstOrDefaultAsync(u => u.RcCustomerId == candidate, ct);
            if (byRc is not null) return byRc;
        }
        return null;
    }

    /// <summary>
    /// True when no already-recorded event for this user has a timestamp &gt;= the incoming one.
    /// Guards against an out-of-order EXPIRATION landing after a newer RENEWAL, etc.
    /// </summary>
    private async Task<bool> IsNewestForUserAsync(Guid userId, long eventTimestampMs, CancellationToken ct)
    {
        var maxSeen = await _db.BillingEvents
            .Where(be => be.UserId == userId)
            .Select(be => (long?)be.EventTimestampMs)
            .MaxAsync(ct);
        return maxSeen is null || eventTimestampMs >= maxSeen.Value;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
