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
    // TRANSFER events carry NO app_user_id/original_app_user_id — instead the entitlement moves
    // between these two arrays of app_user_ids (the losers vs the gainers of the transfer). Every
    // affected id must be re-verified against RevenueCat's REST state (see BillingEventProcessor),
    // never trusted from the payload. Null/absent on non-TRANSFER events.
    [property: JsonPropertyName("transferred_from")] List<string>? TransferredFrom,
    [property: JsonPropertyName("transferred_to")] List<string>? TransferredTo,
    // Entitlements active/affected by this event. Newer RC payloads use the array;
    // the scalar is kept for older integrations.
    [property: JsonPropertyName("entitlement_ids")] string[]? EntitlementIds,
    [property: JsonPropertyName("entitlement_id")] string? EntitlementId,
    // Source-of-truth ordering key (epoch millis). Used for the reorder guard.
    [property: JsonPropertyName("event_timestamp_ms")] long EventTimestampMs,
    [property: JsonPropertyName("expiration_at_ms")] long? ExpirationAtMs,
    [property: JsonPropertyName("product_id")] string? ProductId);
// NOTE: entitlement_ids / event_timestamp_ms are captured for audit/logging only. They are
// NOT trusted to decide the tier — a leaked webhook secret would let an attacker forge them.
// The tier is derived from RevenueCat's REST API (see IRevenueCatClient / BillingEventProcessor).
