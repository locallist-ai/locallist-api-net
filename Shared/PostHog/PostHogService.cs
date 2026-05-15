using System.Text;
using System.Text.Json;

namespace LocalList.API.NET.Shared.PostHog;

/// <summary>
/// Fire-and-forget PostHog server-side event capture via REST.
/// All methods swallow exceptions internally — PostHog failure never breaks the API.
/// Callers use: _ = _posthog.CaptureAsync(...) to avoid blocking the response.
/// </summary>
public class PostHogService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<PostHogService> _logger;

    public PostHogService(HttpClient http, IConfiguration config, ILogger<PostHogService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    /// <summary>Fires a single event for the given distinct_id (user_id Guid string or anon UUID).</summary>
    public async Task CaptureAsync(
        string distinctId,
        string eventName,
        Dictionary<string, object?>? properties = null,
        CancellationToken ct = default)
    {
        var apiKey = _config["PostHog:ApiKey"];
        if (string.IsNullOrEmpty(apiKey)) return;

        try
        {
            var props = properties ?? new Dictionary<string, object?>();
            props["$lib"] = "locallist-api";

            var body = new Dictionary<string, object>
            {
                ["api_key"] = apiKey,
                ["event"] = eventName,
                ["distinct_id"] = distinctId,
                ["properties"] = props
            };

            await SendAsync(body, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PostHog capture failed: event={Event}", eventName);
        }
    }

    /// <summary>Identifies a user (call once after sign-up/sign-in).</summary>
    public async Task IdentifyAsync(
        string distinctId,
        string email,
        string? name = null,
        CancellationToken ct = default)
    {
        var apiKey = _config["PostHog:ApiKey"];
        if (string.IsNullOrEmpty(apiKey)) return;

        try
        {
            var setProps = new Dictionary<string, object?> { ["email"] = email };
            if (!string.IsNullOrEmpty(name)) setProps["name"] = name;

            var body = new Dictionary<string, object>
            {
                ["api_key"] = apiKey,
                ["event"] = "$identify",
                ["distinct_id"] = distinctId,
                ["properties"] = new Dictionary<string, object>
                {
                    ["$set"] = setProps
                }
            };

            await SendAsync(body, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PostHog identify failed: distinctId={Id}", distinctId);
        }
    }

    /// <summary>
    /// Links an anonymous landing UUID to an authenticated user (funnel stitching).
    /// Call once per sign-up/sign-in when the app provides anonymousId from landing.
    /// </summary>
    public async Task AliasAsync(
        string userId,
        string anonymousId,
        CancellationToken ct = default)
    {
        var apiKey = _config["PostHog:ApiKey"];
        if (string.IsNullOrEmpty(apiKey)) return;
        if (string.IsNullOrEmpty(anonymousId)) return;

        try
        {
            var body = new Dictionary<string, object>
            {
                ["api_key"] = apiKey,
                ["event"] = "$create_alias",
                ["distinct_id"] = userId,
                ["properties"] = new Dictionary<string, object>
                {
                    ["alias"] = anonymousId
                }
            };

            await SendAsync(body, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PostHog alias failed: userId={UserId}", userId);
        }
    }

    private async Task SendAsync(Dictionary<string, object> body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("/capture/", content, ct);
        if (!response.IsSuccessStatusCode)
            _logger.LogWarning("PostHog returned {Status}", (int)response.StatusCode);
    }
}
