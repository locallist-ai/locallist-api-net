using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Data;

namespace LocalList.API.NET.Features.Waitlist;

[ApiController]
[Route("waitlist")]
[AllowAnonymous]
[EnableRateLimiting("WaitlistLimit")]
public partial class WaitlistController : ControllerBase
{
    // RFC 5322 simplified — same regex as the Landing serverless function
    [GeneratedRegex(@"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*\.[a-zA-Z]{2,}$")]
    private static partial Regex EmailRegex();

    private readonly LocalListDbContext _db;
    private readonly ILogger<WaitlistController> _logger;
    private readonly IEmailMarketingService _emailMarketing;
    private readonly IConfiguration _configuration;

    public WaitlistController(LocalListDbContext db, ILogger<WaitlistController> logger, IEmailMarketingService emailMarketing, IConfiguration configuration)
    {
        _db = db;
        _logger = logger;
        _emailMarketing = emailMarketing;
        _configuration = configuration;
    }

    [HttpPost]
    public async Task<IActionResult> Join([FromBody] JoinWaitlistRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new { error = "Email is required" });

        var email = request.Email.Trim().ToLowerInvariant();

        if (email.Length > 254 || !EmailRegex().IsMatch(email))
            return BadRequest(new { error = "Invalid email address" });

        try
        {
            var ipRaw = HttpContext.Connection.RemoteIpAddress?.ToString();
            var ipHash = HashIp(ipRaw);
            var userAgent = Request.Headers.UserAgent.ToString();
            if (userAgent.Length > 500) userAgent = userAgent[..500];

            var utmSource = Truncate(request.UtmSource?.Trim(), 100);
            var utmMedium = Truncate(request.UtmMedium?.Trim(), 100);
            var utmCampaign = Truncate(request.UtmCampaign?.Trim(), 100);
            var utmContent = Truncate(request.UtmContent?.Trim(), 100);
            var utmTerm = Truncate(request.UtmTerm?.Trim(), 100);
            var referrer = Truncate(request.Referrer?.Trim(), 500);
            var landingPath = Truncate(request.LandingPath?.Trim(), 500);
            var ttclid = Truncate(request.Ttclid?.Trim(), 200);
            var fbclid = Truncate(request.Fbclid?.Trim(), 200);
            var gclid = Truncate(request.Gclid?.Trim(), 200);

            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO waitlist_entries (
                    email, created_at,
                    utm_source, utm_medium, utm_campaign, utm_content, utm_term,
                    referrer, landing_path, ip_hash, user_agent,
                    ttclid, fbclid, gclid,
                    first_touch_at, last_touch_at
                )
                VALUES (
                    {email}, NOW(),
                    {utmSource}, {utmMedium}, {utmCampaign}, {utmContent}, {utmTerm},
                    {referrer}, {landingPath}, {ipHash}, {userAgent},
                    {ttclid}, {fbclid}, {gclid},
                    NOW(), NOW()
                )
                ON CONFLICT (email) DO UPDATE SET
                    last_touch_at = NOW(),
                    utm_source = EXCLUDED.utm_source,
                    utm_medium = EXCLUDED.utm_medium,
                    utm_campaign = EXCLUDED.utm_campaign,
                    utm_content = EXCLUDED.utm_content,
                    utm_term = EXCLUDED.utm_term,
                    referrer = EXCLUDED.referrer,
                    landing_path = EXCLUDED.landing_path,
                    ip_hash = EXCLUDED.ip_hash,
                    user_agent = EXCLUDED.user_agent,
                    ttclid = EXCLUDED.ttclid,
                    fbclid = EXCLUDED.fbclid,
                    gclid = EXCLUDED.gclid
                """, ct);

            var count = await _db.WaitlistEntries.CountAsync(ct);

            _logger.LogInformation("Waitlist signup processed for {EmailPrefix}", email[..Math.Min(3, email.Length)] + "***");

            // Send to Klaviyo (failure must not block signup)
            try
            {
                var utmData = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(utmSource)) utmData["source"] = utmSource;
                if (!string.IsNullOrEmpty(utmMedium)) utmData["medium"] = utmMedium;
                if (!string.IsNullOrEmpty(utmCampaign)) utmData["campaign"] = utmCampaign;
                if (!string.IsNullOrEmpty(utmContent)) utmData["content"] = utmContent;

                await _emailMarketing.AddToWaitlistAsync(email, utmData.Count > 0 ? utmData : null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Klaviyo failed for {EmailPrefix}", email[..Math.Min(3, email.Length)] + "***");
            }

            return StatusCode(201, new JoinWaitlistResponse("Successfully joined the waitlist", count));
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Waitlist signup failed");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    [HttpGet("count")]
    [ResponseCache(Duration = 60)]
    public async Task<IActionResult> Count(CancellationToken ct)
    {
        var count = await _db.WaitlistEntries.CountAsync(ct);
        return Ok(new WaitlistCountResponse(count));
    }

    private string HashIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return string.Empty;
        var salt = _configuration["WAITLIST_IP_SALT"] ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes($"{salt}:{ip}");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is { Length: > 0 } v && v.Length > maxLength ? v[..maxLength] : value;
}
