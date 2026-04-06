using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalList.API.NET.Features.Waitlist;

public class KlaviyoService : IEmailMarketingService
{
    private readonly HttpClient _http;
    private readonly ILogger<KlaviyoService> _logger;
    private readonly string? _apiKey;
    private readonly string? _waitlistListId;

    public KlaviyoService(HttpClient http, ILogger<KlaviyoService> logger, IConfiguration configuration)
    {
        _http = http;
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable("KLAVIYO_API_KEY")
                  ?? configuration["Klaviyo:ApiKey"];
        _waitlistListId = Environment.GetEnvironmentVariable("KLAVIYO_WAITLIST_LIST_ID")
                          ?? configuration["Klaviyo:WaitlistListId"];

        _http.BaseAddress = new Uri("https://a.klaviyo.com/");
        _http.DefaultRequestHeaders.Add("revision", "2024-10-15");
    }

    public async Task AddToWaitlistAsync(string email, Dictionary<string, string>? utmData = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(_waitlistListId))
        {
            _logger.LogDebug("Klaviyo not configured (missing API key or list ID), skipping email marketing");
            return;
        }

        try
        {
            // Step 1: Create or update the profile
            var profileId = await UpsertProfileAsync(email, utmData, ct);

            if (profileId is null)
            {
                _logger.LogWarning("Failed to upsert Klaviyo profile for {EmailPrefix}", email[..Math.Min(3, email.Length)] + "***");
                return;
            }

            // Step 2: Add profile to the Waitlist list
            await AddToListAsync(profileId, ct);

            _logger.LogInformation("Klaviyo: added {EmailPrefix} to waitlist list", email[..Math.Min(3, email.Length)] + "***");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Klaviyo integration failed for {EmailPrefix}", email[..Math.Min(3, email.Length)] + "***");
        }
    }

    private async Task<string?> UpsertProfileAsync(string email, Dictionary<string, string>? utmData, CancellationToken ct)
    {
        var properties = new Dictionary<string, object> { ["source"] = "waitlist" };

        if (utmData is not null)
        {
            foreach (var (key, value) in utmData)
            {
                if (!string.IsNullOrEmpty(value))
                    properties[$"utm_{key}"] = value;
            }
        }

        var payload = new
        {
            data = new
            {
                type = "profile",
                attributes = new
                {
                    email,
                    properties
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/profiles/");
        request.Headers.Add("Authorization", $"Klaviyo-API-Key {_apiKey}");
        request.Content = JsonContent.Create(payload, options: JsonOptions);

        var response = await _http.SendAsync(request, ct);

        // 201 = created, 409 = already exists (need to get existing profile ID)
        if (response.StatusCode == System.Net.HttpStatusCode.Created)
        {
            var result = await response.Content.ReadFromJsonAsync<KlaviyoResponse>(JsonOptions, ct);
            return result?.Data?.Id;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Profile already exists — extract ID from the duplicate error
            var error = await response.Content.ReadFromJsonAsync<KlaviyoErrorResponse>(JsonOptions, ct);
            var existingId = error?.Errors?.FirstOrDefault()?.Meta?.DuplicateProfileId;
            return existingId;
        }

        _logger.LogWarning("Klaviyo profile upsert returned {StatusCode}", response.StatusCode);
        return null;
    }

    private async Task AddToListAsync(string profileId, CancellationToken ct)
    {
        var payload = new
        {
            data = new[]
            {
                new
                {
                    type = "profile",
                    id = profileId
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/lists/{_waitlistListId}/relationships/profiles/");
        request.Headers.Add("Authorization", $"Klaviyo-API-Key {_apiKey}");
        request.Content = JsonContent.Create(payload, options: JsonOptions);

        var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Klaviyo add-to-list returned {StatusCode}", response.StatusCode);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Response models for Klaviyo API
    private record KlaviyoResponse(KlaviyoData? Data);
    private record KlaviyoData(string? Id);
    private record KlaviyoErrorResponse(List<KlaviyoError>? Errors);
    private record KlaviyoError(KlaviyoErrorMeta? Meta);
    private record KlaviyoErrorMeta(string? DuplicateProfileId);
}
