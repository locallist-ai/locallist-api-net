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
    /// <summary>
    /// A TRANSFER event was applied: every resolved user in transferred_from/transferred_to had
    /// its tier re-derived from RevenueCat's REST state (origin typically → free, destination →
    /// pro). At least one local user resolved. See <see cref="BillingEventProcessor"/>.
    /// </summary>
    Transferred,
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
/// gate the payload is still attacker-shaped, so the tier is derived from RevenueCat's
/// authoritative REST state (<see cref="IRevenueCatClient"/>), never from the payload's
/// entitlement/timestamp.
///
/// CRITICAL — the payload must NOT be able to decouple "whose entitlement we verify" from "whom
/// we credit". So we (1) resolve the local <see cref="User"/> FIRST from the payload identifiers,
/// then (2) verify against RevenueCat EXCLUSIVELY that user's OWN identifiers (its <c>Id</c> and
/// its already-linked <c>RcCustomerId</c>) — never a raw <c>app_user_id</c> string from the
/// payload. A forged event with <c>app_user_id</c>=(some active RC id) and
/// <c>original_app_user_id</c>=(attacker's Guid) therefore cannot grant the attacker "pro":
/// we only ask RevenueCat about the attacker's own ids, which are not entitled.
///
/// Idempotent (deduped by <see cref="BillingEvent.RcEventId"/> + its UNIQUE index race guard,
/// scoped to that constraint only) and transactional (ledger insert + tier write commit together).
/// Slice-local service (VSA): lives in Features/Billing, imports no other slice.
///
/// TRANSFER events: a subscription moving between App User IDs (Restore on a shared Apple ID,
/// resold device, Family Sharing…) does NOT carry app_user_id/original_app_user_id — it carries
/// <c>transferred_from</c>/<c>transferred_to</c> arrays. Each id in BOTH arrays is resolved to a
/// local user and its tier re-derived from RevenueCat's REST state EXACTLY like a normal event
/// (own-ids-only verification — a forged transfer cannot grant what RC does not confirm). The
/// typical outcome: origin → free, destination → pro. If RC REST is unavailable for ANY affected
/// user the whole event is retryable (503, nothing written) — no half-applied transfer. A single
/// ledger row is written per event (the table has one <c>UserId</c>); it is attributed to the
/// resolved destination (falling back to origin), with the full from/to lists in <c>AppUserId</c>
/// for audit.
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
    ///
    /// Concurrency note: there is no per-user serialization, so two events for the SAME user
    /// processed concurrently write the tier in commit order. This is self-correcting — the next
    /// event (or a re-verification) re-derives the tier from RevenueCat's current state — and the
    /// window is tiny (RevenueCat delivers a user's events sequentially in practice). Optimistic
    /// row-versioning was judged not worth the migration for this volume.
    /// </summary>
    public async Task<BillingEventOutcome> ProcessAsync(
        RevenueCatEvent evt, string plusEntitlementId, CancellationToken ct)
    {
        var rcEventId = evt.Id!; // validated non-empty by the caller
        var payloadAppUserId = evt.AppUserId ?? evt.OriginalAppUserId; // audit only

        // Fast-path dedup: if we've already recorded this event id, do nothing.
        if (await _db.BillingEvents.AnyAsync(be => be.RcEventId == rcEventId, ct))
        {
            _logger.LogInformation("RevenueCat event {EventId} already processed; skipping", rcEventId);
            return BillingEventOutcome.Duplicate;
        }

        // TRANSFER events don't carry app_user_id — they move the entitlement between the
        // transferred_from/transferred_to arrays. Handle them on a dedicated multi-user path.
        if (IsTransfer(evt))
            return await ProcessTransferAsync(evt, plusEntitlementId, rcEventId, ct);

        // (1) Resolve WHOM to credit first, from the payload identifiers.
        var user = await ResolveUserAsync(evt, ct);

        BillingEventOutcome outcome;
        if (user is null)
        {
            outcome = BillingEventOutcome.UserNotFound;
            _logger.LogWarning(
                "RevenueCat event {EventId} ({Type}): no user for app_user_id {AppUserId}; recorded unresolved",
                rcEventId, evt.Type, payloadAppUserId);
        }
        else
        {
            // (2) Verify against RevenueCat using ONLY this user's OWN identifiers. The payload's
            //     app_user_id is deliberately NOT used as the lookup key — that is the god-token.
            var status = await VerifyAnyActiveAsync(user, plusEntitlementId, ct);

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
            AppUserId = payloadAppUserId,
            EventType = evt.Type ?? "UNKNOWN",
            EventTimestampMs = ClampTimestamp(evt.EventTimestampMs),
            ProcessedAt = _clock.GetUtcNow(),
        });

        var result = await SaveLedgerAsync(rcEventId, outcome, ct);
        _logger.LogInformation(
            "RevenueCat event {EventId} ({Type}) processed for user {UserId}: {Outcome}",
            rcEventId, evt.Type, user?.Id, result);
        return result;
    }

    /// <summary>
    /// Handles a TRANSFER event. RevenueCat moves the entitlement between the
    /// <c>transferred_from</c> and <c>transferred_to</c> App User IDs; there is no single
    /// app_user_id to credit. For EVERY id in both arrays we resolve the local user and re-derive
    /// its tier from RevenueCat's REST state using ONLY that user's own ids (same guarantee as the
    /// single-event path — the payload can never grant what RC does not confirm).
    ///
    /// All-or-nothing on RC availability: we verify every affected user FIRST and only mutate tiers
    /// once none came back Unavailable, so a mid-transfer RC outage leaves ZERO partial state and
    /// the event is retried (503, unrecorded). One ledger row per event, attributed to the resolved
    /// destination (fallback origin), with both id lists captured in <c>AppUserId</c> for audit.
    /// </summary>
    private async Task<BillingEventOutcome> ProcessTransferAsync(
        RevenueCatEvent evt, string plusEntitlementId, string rcEventId, CancellationToken ct)
    {
        // Resolve destination ids first (so the ledger row is attributed to the gainer), then
        // origin. Dedup by User.Id — the same user could appear more than once across the arrays.
        var resolved = new Dictionary<Guid, User>();
        var unresolved = new List<string>();
        User? destination = null;
        User? origin = null;

        foreach (var candidate in evt.TransferredTo ?? Enumerable.Empty<string>())
        {
            var u = await ResolveSingleAsync(candidate, ct);
            if (u is null) { if (!string.IsNullOrWhiteSpace(candidate)) unresolved.Add(candidate); continue; }
            destination ??= u;
            resolved[u.Id] = u;
        }
        foreach (var candidate in evt.TransferredFrom ?? Enumerable.Empty<string>())
        {
            var u = await ResolveSingleAsync(candidate, ct);
            if (u is null) { if (!string.IsNullOrWhiteSpace(candidate)) unresolved.Add(candidate); continue; }
            origin ??= u;
            resolved[u.Id] = u;
        }

        // Verify EVERY resolved user against RevenueCat BEFORE touching any tier. If any lookup is
        // Unavailable, bail out retryable without writing tiers or the ledger row (no partial state).
        var verified = new List<(User user, RevenueCatEntitlementStatus status)>(resolved.Count);
        foreach (var user in resolved.Values)
        {
            var status = await VerifyAnyActiveAsync(user, plusEntitlementId, ct);
            if (status == RevenueCatEntitlementStatus.Unavailable)
            {
                _logger.LogWarning(
                    "RevenueCat TRANSFER {EventId}: RC state unavailable verifying user {UserId}; " +
                    "no tier written, will retry",
                    rcEventId, user.Id);
                return BillingEventOutcome.RcUnavailable;
            }
            verified.Add((user, status));
        }

        // All affected users verified — now apply the RC-derived tiers.
        foreach (var (user, status) in verified)
        {
            var desired = status == RevenueCatEntitlementStatus.Active ? TierPro : TierFree;
            if (user.Tier != desired) user.Tier = desired;
            user.UpdatedAt = _clock.GetUtcNow();
        }

        if (unresolved.Count > 0)
        {
            _logger.LogWarning(
                "RevenueCat TRANSFER {EventId}: {Count} transferred id(s) mapped to no LocalList user: {Ids}",
                rcEventId, unresolved.Count, string.Join(", ", unresolved));
        }

        var attributed = destination ?? origin;
        _db.BillingEvents.Add(new BillingEvent
        {
            RcEventId = rcEventId,
            UserId = attributed?.Id,
            AppUserId = BuildTransferAudit(evt),
            EventType = evt.Type ?? "TRANSFER",
            EventTimestampMs = ClampTimestamp(evt.EventTimestampMs),
            ProcessedAt = _clock.GetUtcNow(),
        });

        // If not a single id resolved there is nothing to credit — record as unresolved.
        var outcome = resolved.Count > 0 ? BillingEventOutcome.Transferred : BillingEventOutcome.UserNotFound;
        var result = await SaveLedgerAsync(rcEventId, outcome, ct);
        _logger.LogInformation(
            "RevenueCat TRANSFER {EventId} processed: {Resolved} user(s) re-verified, {Unresolved} unresolved → {Outcome}",
            rcEventId, resolved.Count, unresolved.Count, result);
        return result;
    }

    /// <summary>
    /// Commits the tracked tier writes + the ledger row atomically. Returns <paramref name="outcome"/>
    /// on success, or <see cref="BillingEventOutcome.Duplicate"/> when a concurrent delivery of the
    /// SAME event id won the INSERT race on the rc_event_id unique index (the tier writes in THIS
    /// transaction roll back with it — the winner already applied the identical, RC-derived effect).
    /// Only that specific constraint is swallowed; any other unique violation propagates.
    /// </summary>
    private async Task<BillingEventOutcome> SaveLedgerAsync(
        string rcEventId, BillingEventOutcome outcome, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return outcome;
        }
        catch (DbUpdateException ex) when (IsDuplicateEventRace(ex))
        {
            _logger.LogInformation(
                "RevenueCat event {EventId} lost the insert race (concurrent duplicate); treating as processed",
                rcEventId);
            return BillingEventOutcome.Duplicate;
        }
    }

    /// <summary>True when this is a TRANSFER event (by type, or by populated transfer arrays).</summary>
    private static bool IsTransfer(RevenueCatEvent evt) =>
        string.Equals(evt.Type, "TRANSFER", StringComparison.OrdinalIgnoreCase) ||
        evt.TransferredFrom is { Count: > 0 } ||
        evt.TransferredTo is { Count: > 0 };

    /// <summary>Compact from/to audit string for the ledger row, truncated to the column width.</summary>
    private static string BuildTransferAudit(RevenueCatEvent evt)
    {
        var from = evt.TransferredFrom is { Count: > 0 } ? string.Join(",", evt.TransferredFrom) : "-";
        var to = evt.TransferredTo is { Count: > 0 } ? string.Join(",", evt.TransferredTo) : "-";
        var s = $"transfer from=[{from}] to=[{to}]";
        return s.Length > 255 ? s[..255] : s;
    }

    /// <summary>
    /// Asks RevenueCat whether the entitlement is active for any of the user's OWN identifiers
    /// (its User.Id and, if already linked, its RcCustomerId). Active wins; if none is active but
    /// any lookup was Unavailable, the whole check is Unavailable (retry) rather than a false free.
    /// </summary>
    private async Task<RevenueCatEntitlementStatus> VerifyAnyActiveAsync(
        User user, string entitlementId, CancellationToken ct)
    {
        var ownIds = new List<string>(2) { user.Id.ToString() };
        if (!string.IsNullOrEmpty(user.RcCustomerId) &&
            !string.Equals(user.RcCustomerId, user.Id.ToString(), StringComparison.Ordinal))
        {
            ownIds.Add(user.RcCustomerId!);
        }

        var anyUnavailable = false;
        foreach (var id in ownIds)
        {
            var status = await _revenueCat.GetEntitlementStatusAsync(id, entitlementId, ct);
            if (status == RevenueCatEntitlementStatus.Active)
                return RevenueCatEntitlementStatus.Active;
            if (status == RevenueCatEntitlementStatus.Unavailable)
                anyUnavailable = true;
        }

        return anyUnavailable
            ? RevenueCatEntitlementStatus.Unavailable
            : RevenueCatEntitlementStatus.Inactive;
    }

    /// <summary>
    /// Maps a RevenueCat app_user_id to a LocalList user. The app sets app_user_id to the
    /// User.Id (Guid); a separately-linked flow may populate rc_customer_id. Tries both the
    /// primary and original identifiers to FIND the user — but note the tier decision is then
    /// verified against the user's OWN ids only (see <see cref="VerifyAnyActiveAsync"/>), so
    /// resolving via original_app_user_id cannot be leveraged to credit an unverified identity.
    /// </summary>
    private async Task<User?> ResolveUserAsync(RevenueCatEvent evt, CancellationToken ct)
    {
        foreach (var candidate in new[] { evt.AppUserId, evt.OriginalAppUserId })
        {
            var user = await ResolveSingleAsync(candidate, ct);
            if (user is not null) return user;
        }
        return null;
    }

    /// <summary>
    /// Maps ONE RevenueCat app_user_id string to a local user: Guid → <see cref="User.Id"/> first,
    /// then <see cref="User.RcCustomerId"/>. Shared by the single-event and TRANSFER paths. As with
    /// <see cref="ResolveUserAsync"/>, resolving here only decides WHICH user is affected — the tier
    /// is still verified against that user's OWN ids (see <see cref="VerifyAnyActiveAsync"/>).
    /// </summary>
    private async Task<User?> ResolveSingleAsync(string? candidate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        if (Guid.TryParse(candidate, out var userId))
        {
            var byId = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (byId is not null) return byId;
        }

        return await _db.Users.FirstOrDefaultAsync(u => u.RcCustomerId == candidate, ct);
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
