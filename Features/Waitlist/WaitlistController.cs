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

    public WaitlistController(LocalListDbContext db, ILogger<WaitlistController> logger, IEmailMarketingService emailMarketing)
    {
        _db = db;
        _logger = logger;
        _emailMarketing = emailMarketing;
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
            // Use raw SQL for INSERT ... ON CONFLICT DO NOTHING (EF Core doesn't support this natively)
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO waitlist_entries (email, created_at) VALUES ({email}, NOW()) ON CONFLICT (email) DO NOTHING", ct);

            // Same response for new or duplicate — prevents email enumeration
            var count = await _db.WaitlistEntries.CountAsync(ct);

            _logger.LogInformation("Waitlist signup processed for {EmailPrefix}", email[..Math.Min(3, email.Length)] + "***");

            // Fire-and-forget: send to Klaviyo (failure must not block signup)
            _ = Task.Run(async () =>
            {
                try
                {
                    var utmData = new Dictionary<string, string>();
                    if (!string.IsNullOrEmpty(request.UtmSource)) utmData["source"] = request.UtmSource;
                    if (!string.IsNullOrEmpty(request.UtmMedium)) utmData["medium"] = request.UtmMedium;
                    if (!string.IsNullOrEmpty(request.UtmCampaign)) utmData["campaign"] = request.UtmCampaign;
                    if (!string.IsNullOrEmpty(request.UtmContent)) utmData["content"] = request.UtmContent;

                    await _emailMarketing.AddToWaitlistAsync(email, utmData.Count > 0 ? utmData : null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Klaviyo background task failed for {EmailPrefix}", email[..Math.Min(3, email.Length)] + "***");
                }
            }, CancellationToken.None);

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
}
