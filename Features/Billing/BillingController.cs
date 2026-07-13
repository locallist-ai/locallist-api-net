using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LocalList.API.NET.Features.Billing;

/// <summary>
/// Receives RevenueCat subscriber webhooks and drives <see cref="Shared.Data.Entities.User.Tier"/>.
/// This is the server-side source of truth for entitlement state: the app polls GET /account
/// after a purchase and expects the tier to flip here. Anonymous at the transport level
/// (server-to-server, no JWT) but gated by a shared-secret Authorization header.
/// </summary>
[ApiController]
[Route("webhooks/revenuecat")]
[AllowAnonymous]
public class BillingController : ControllerBase
{
    // Config keys. Env var wins (Railway secret), config fallback for local dev — same
    // idiom as JWT_SECRET / KLAVIYO_API_KEY elsewhere in the codebase.
    // TODO(pablo): RevenueCat webhook auth secret — set REVENUECAT_WEBHOOK_AUTH (Railway) to
    // the exact "Authorization" header value configured in the RevenueCat dashboard
    // (Project settings → Integrations → Webhooks → Authorization header). Fail-closed until set.
    private const string AuthEnvVar = "REVENUECAT_WEBHOOK_AUTH";
    private const string AuthConfigKey = "RevenueCat:WebhookAuthToken";
    // The RevenueCat entitlement identifier that maps to LocalList "pro". Configurable so
    // product can rename it without a redeploy; defaults to "plus".
    private const string EntitlementConfigKey = "RevenueCat:PlusEntitlementId";
    private const string DefaultPlusEntitlementId = "plus";

    private readonly BillingEventProcessor _processor;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BillingController> _logger;

    public BillingController(
        BillingEventProcessor processor, IConfiguration configuration, ILogger<BillingController> logger)
    {
        _processor = processor;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Handle([FromBody] RevenueCatWebhookRequest request, CancellationToken ct)
    {
        var expected = Environment.GetEnvironmentVariable(AuthEnvVar) ?? _configuration[AuthConfigKey];

        // Fail closed: if no secret is configured, reject every request rather than trusting
        // an unauthenticated caller to mutate tiers.
        if (string.IsNullOrEmpty(expected))
        {
            _logger.LogError(
                "RevenueCat webhook rejected: {EnvVar} / {ConfigKey} is not configured (fail-closed)",
                AuthEnvVar, AuthConfigKey);
            return StatusCode(503, new { error = "Webhook not configured" });
        }

        var provided = Request.Headers.Authorization.ToString();
        if (!FixedTimeEquals(provided, expected))
        {
            _logger.LogWarning("RevenueCat webhook rejected: invalid Authorization header");
            return Unauthorized(new { error = "Invalid webhook authorization" });
        }

        var evt = request.Event;
        if (evt is null || string.IsNullOrEmpty(evt.Id) || string.IsNullOrEmpty(evt.Type))
        {
            _logger.LogWarning("RevenueCat webhook rejected: missing event id/type");
            return BadRequest(new { error = "Malformed event" });
        }

        var plusEntitlementId = _configuration[EntitlementConfigKey] ?? DefaultPlusEntitlementId;

        var outcome = await _processor.ProcessAsync(evt, plusEntitlementId, ct);

        // Always 200 for an authorized, well-formed event (including no-ops) so RevenueCat
        // does not retry a delivery we have already durably recorded.
        return Ok(new { received = true, outcome = outcome.ToString() });
    }

    /// <summary>Constant-time comparison over UTF-8 bytes; length-safe (no early exit on length).</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
