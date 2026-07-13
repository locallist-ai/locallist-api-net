using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.NET.Features.Billing;

/// <summary>Outcome of processing a single RevenueCat event — used for logging/tests and status mapping.</summary>
public enum BillingEventOutcome
{
    /// <summary>Event id already in the ledger — no-op (idempotent replay).</summary>
    Duplicate,
    /// <summary>RevenueCat confirms the entitlement is active → user set/kept "pro".</summary>
    GrantedPro,
    /// <summary>RevenueCat confirms the entitlement is inactive → user set/kept "free".</summary>
    RevokedToFree,
    /// <summary>Recorded with a null user — the app_user_id did not map to any LocalList user.</summary>
    UserNotFound,
    /// <summary>
    /// RevenueCat could not be verified (down / not configured). Tier UNCHANGED and the event
    /// is NOT recorded, so the caller returns a retryable error and RevenueCat re-delivers.
    /// </summary>
    RcUnavailable,
}

/// <summary>
/// Applies RevenueCat subscriber events to <see cref="User.Tier"/>. This is the ONLY
/// server-side writer of the tier column driven by billing.
///
/// SECURITY MODEL: the webhook is a TRIGGER, not the source of truth. Past the shared-secret
/// gate, the payload is still attacker-shaped (a leaked secret lets anyone POST an arbitrary
/// app_user_id / entitlement / event_timestamp_ms). So the tier is derived from RevenueCat's
/// authoritative REST state (<see cref="IRevenueCatClient"/>), never from the payload. A forged
/// grant naming a victim, or a pinned <c>event_timestamp_ms=long.MaxValue</c>, therefore cannot
/// grant or freeze "pro": we ask RevenueCat what the subscriber actually has, right now.
///
/// Idempotent (deduped by <see cref="BillingEvent.RcEventId"/> + its UNIQUE index race guard,
/// scoped to that constraint only) and transactional (ledger insert + tier write commit together).
/// Slice-local service (VSA): lives in Features/Billing, imports no other slice.
/// </summary>
public class BillingEventProcessor
{
    private const string TierPro = "pro";
    private const string TierFree = "free";
    private const string RcEventIdIndexName = "IX_billing_events_rc_event_id";

    // Defense-in-depth: an absurdly-future event_timestamp_ms is stored clamped so it can never
    // pollute audit queries. It no longer drives any tier decision (RevenueCat state does).
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromHours(24);

    private readonly LocalListDbContext _db;
    private readonly IRevenueCatClient _revenueCat;
    private readonly TimeProvider _clock;
    private readonly ILogger<BillingEventProcessor> _logger;

    public BillingEventProcessor(
        LocalListDbContext db,
        IRevenueCatClient revenueCat,
        TimeProvider clock,
        ILogger<BillingEventProcessor> logger)
    {
        _db = db;
        _revenueCat = revenueCat;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Processes one event. Assumes the caller already verified the webhook authorization.
    /// Returns the outcome; never throws for business cases (only for infra failures other than
    /// the deduped-insert race).
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

        BillingEventOutcome outcome;
        if (user is null)
        {
            outcome = BillingEventOutcome.UserNotFound;
            _logger.LogWarning(
                "RevenueCat event {EventId} ({Type}): no user for app_user_id {AppUserId}; recorded unresolved",
                rcEventId, evt.Type, appUserId);
        }
        else
        {
            // Ask RevenueCat for the CURRENT, verified state — ignore the payload's claims.
            var status = await _revenueCat.GetEntitlementStatusAsync(appUserId!, plusEntitlementId, ct);

            if (status == RevenueCatEntitlementStatus.Unavailable)
            {
                // Do NOT change the tier and do NOT record the event: return a retryable signal
                // so RevenueCat re-delivers once its API is reachable again. Never grant blindly.
                _logger.LogWarning(
                    "RevenueCat event {EventId} ({Type}) for user {UserId}: RC state unavailable; tier unchanged, will retry",
                    rcEventId, evt.Type, user.Id);
                return BillingEventOutcome.RcUnavailable;
            }

            if (status == RevenueCatEntitlementStatus.Active)
            {
                if (user.Tier != TierPro) user.Tier = TierPro;
                LinkRcCustomer(user, appUserId);
                user.UpdatedAt = _clock.GetUtcNow();
                outcome = BillingEventOutcome.GrantedPro;
            }
            else
            {
                if (user.Tier != TierFree) user.Tier = TierFree;
                user.UpdatedAt = _clock.GetUtcNow();
                outcome = BillingEventOutcome.RevokedToFree;
            }
        }

        _db.BillingEvents.Add(new BillingEvent
        {
            RcEventId = rcEventId,
            UserId = user?.Id,
            AppUserId = appUserId,
            EventType = evt.Type ?? "UNKNOWN",
            EventTimestampMs = ClampTimestamp(evt.EventTimestampMs),
            ProcessedAt = _clock.GetUtcNow(),
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateEventRace(ex))
        {
            // A concurrent delivery of the SAME event id won the INSERT race on the
            // rc_event_id unique index. The tier write in THIS transaction rolls back with
            // it — the winner already applied the identical, RC-derived effect. Idempotent.
            // NOTE: only this specific constraint is swallowed. Any other unique violation
            // (e.g. a rc_customer_id collision) must propagate — never silently 200 a
            // legitimate upgrade into a lost purchase.
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
    /// Persists the RevenueCat customer link so /account and future lookups resolve — but only
    /// when it is free to take. If another user already holds this rc_customer_id we skip the
    /// assignment (and log) rather than provoke a unique-violation that would poison retries;
    /// the tier grant itself still applies.
    /// </summary>
    private void LinkRcCustomer(User user, string? appUserId)
    {
        if (string.IsNullOrEmpty(appUserId) || !string.IsNullOrEmpty(user.RcCustomerId))
            return;

        var takenByOther = _db.Users.Any(u => u.RcCustomerId == appUserId && u.Id != user.Id);
        if (takenByOther)
        {
            _logger.LogWarning(
                "rc_customer_id {AppUserId} already linked to another user; not linking to {UserId}",
                appUserId, user.Id);
            return;
        }

        user.RcCustomerId = appUserId;
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

    /// <summary>Clamps an absurdly-future timestamp to "now" for audit sanity (see field doc).</summary>
    private long ClampTimestamp(long eventTimestampMs)
    {
        var ceiling = _clock.GetUtcNow().Add(FutureTolerance).ToUnixTimeMilliseconds();
        return eventTimestampMs > ceiling ? _clock.GetUtcNow().ToUnixTimeMilliseconds() : eventTimestampMs;
    }

    private static bool IsDuplicateEventRace(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg &&
        pg.ConstraintName == RcEventIdIndexName;
}
