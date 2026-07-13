using System.Text.Json.Serialization;

namespace LocalList.API.NET.Features.Billing;

/// <summary>
/// Top-level RevenueCat webhook payload. RevenueCat POSTs a single event wrapped in
/// this envelope (docs: https://www.revenuecat.com/docs/webhooks).
/// </summary>
public record RevenueCatWebhookRequest(
    [property: JsonPropertyName("api_version")] string? ApiVersion,
    [property: JsonPropertyName("event")] RevenueCatEvent? Event);

/// <summary>
/// A single RevenueCat subscriber event. Only the fields this slice needs are mapped;
/// unknown fields are ignored by System.Text.Json.
/// </summary>
public record RevenueCatEvent(
    // Globally unique per event — the idempotency key.
    [property: JsonPropertyName("id")] string? Id,
    // INITIAL_PURCHASE | RENEWAL | CANCELLATION | EXPIRATION | PRODUCT_CHANGE | ...
    [property: JsonPropertyName("type")] string? Type,
    // The app-set identifier passed to Purchases.configure(appUserID:) at purchase time.
    // For LocalList this is the User.Id (Guid) string.
    [property: JsonPropertyName("app_user_id")] string? AppUserId,
    // Present after an anonymous→identified merge or transfer.
    [property: JsonPropertyName("original_app_user_id")] string? OriginalAppUserId,
    // Entitlements active/affected by this event. Newer RC payloads use the array;
    // the scalar is kept for older integrations.
    [property: JsonPropertyName("entitlement_ids")] string[]? EntitlementIds,
    [property: JsonPropertyName("entitlement_id")] string? EntitlementId,
    // Source-of-truth ordering key (epoch millis). Used for the reorder guard.
    [property: JsonPropertyName("event_timestamp_ms")] long EventTimestampMs,
    [property: JsonPropertyName("expiration_at_ms")] long? ExpirationAtMs,
    [property: JsonPropertyName("product_id")] string? ProductId)
{
    /// <summary>True when the "plus" entitlement (id configurable) is affected by this event.</summary>
    public bool AffectsEntitlement(string entitlementId)
    {
        if (EntitlementIds is { Length: > 0 } ids &&
            ids.Any(id => string.Equals(id, entitlementId, StringComparison.OrdinalIgnoreCase)))
            return true;
        return string.Equals(EntitlementId, entitlementId, StringComparison.OrdinalIgnoreCase);
    }
}
