namespace LocalList.API.NET.Features.Billing;

/// <summary>
/// Authoritative entitlement state for a subscriber, as reported by RevenueCat's REST API.
/// The webhook payload is NOT trusted for this — see <see cref="IRevenueCatClient"/>.
/// </summary>
public enum RevenueCatEntitlementStatus
{
    /// <summary>The entitlement is present and not expired → user should be "pro".</summary>
    Active,
    /// <summary>The entitlement is absent or expired → user should be "free".</summary>
    Inactive,
    /// <summary>RevenueCat could not be reached / not configured. Tier must NOT be changed.</summary>
    Unavailable,
}

/// <summary>
/// Reads the CURRENT, server-verified entitlement state for an app_user_id from RevenueCat.
/// The webhook is only a trigger; this is the source of truth, so a forged/reordered payload
/// (spoofed app_user_id, entitlement, or event_timestamp_ms) cannot move a user's tier.
/// </summary>
public interface IRevenueCatClient
{
    Task<RevenueCatEntitlementStatus> GetEntitlementStatusAsync(
        string appUserId, string entitlementId, CancellationToken ct);
}
