using System.Net;
using System.Text.Json;

namespace LocalList.API.NET.Features.Billing;

/// <summary>
/// HTTP client for RevenueCat's v1 REST API. Calls <c>GET /subscribers/{app_user_id}</c> with a
/// SECRET API key and derives whether the given entitlement is currently active. This is the
/// authoritative check that replaces trusting the webhook payload.
/// </summary>
public class RevenueCatClient : IRevenueCatClient
{
    // TODO(pablo): RevenueCat REST API key — set REVENUECAT_REST_API_KEY (Railway) to a SECRET
    // API key from the RevenueCat dashboard (Project settings → API keys → "Secret" key, sk_...).
    // WITHOUT it the client returns Unavailable and NO tier change is applied (fail-safe: never
    // grant blindly). It is a different secret from the webhook Authorization header.
    private const string ApiKeyEnvVar = "REVENUECAT_REST_API_KEY";
    private const string ApiKeyConfigKey = "RevenueCat:RestApiKey";
    private const string DefaultBaseUrl = "https://api.revenuecat.com/v1";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _clock;
    private readonly ILogger<RevenueCatClient> _logger;

    public RevenueCatClient(
        HttpClient http, IConfiguration configuration, TimeProvider clock, ILogger<RevenueCatClient> logger)
    {
        _http = http;
        _configuration = configuration;
        _clock = clock;
        _logger = logger;
    }

    public async Task<RevenueCatEntitlementStatus> GetEntitlementStatusAsync(
        string appUserId, string entitlementId, CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvVar) ?? _configuration[ApiKeyConfigKey];
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError(
                "RevenueCat REST key not configured ({EnvVar}/{ConfigKey}); cannot verify subscriber — treating as Unavailable",
                ApiKeyEnvVar, ApiKeyConfigKey);
            return RevenueCatEntitlementStatus.Unavailable;
        }

        var baseUrl = _configuration["RevenueCat:RestBaseUrl"] ?? DefaultBaseUrl;
        var url = $"{baseUrl}/subscribers/{Uri.EscapeDataString(appUserId)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            using var response = await _http.SendAsync(request, ct);

            // 404 = RevenueCat has no such subscriber → definitively not entitled.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return RevenueCatEntitlementStatus.Inactive;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "RevenueCat REST returned {Status} for subscriber lookup; treating as Unavailable",
                    (int)response.StatusCode);
                return RevenueCatEntitlementStatus.Unavailable;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            return IsEntitlementActive(doc.RootElement, entitlementId)
                ? RevenueCatEntitlementStatus.Active
                : RevenueCatEntitlementStatus.Inactive;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Network failure, timeout, or unparseable body → do not guess; caller must not
            // change the tier on Unavailable.
            _logger.LogWarning(ex, "RevenueCat REST lookup failed; treating as Unavailable");
            return RevenueCatEntitlementStatus.Unavailable;
        }
    }

    /// <summary>
    /// True when <c>subscriber.entitlements[entitlementId]</c> exists and its
    /// <c>expires_date</c> is null (lifetime) or in the future.
    /// </summary>
    private bool IsEntitlementActive(JsonElement root, string entitlementId)
    {
        if (!root.TryGetProperty("subscriber", out var subscriber) ||
            !subscriber.TryGetProperty("entitlements", out var entitlements) ||
            entitlements.ValueKind != JsonValueKind.Object ||
            !entitlements.TryGetProperty(entitlementId, out var ent))
        {
            return false;
        }

        if (!ent.TryGetProperty("expires_date", out var expires) ||
            expires.ValueKind == JsonValueKind.Null)
        {
            // No expiry → lifetime/non-expiring entitlement.
            return true;
        }

        return expires.TryGetDateTimeOffset(out var expiresAt) && expiresAt > _clock.GetUtcNow();
    }
}
