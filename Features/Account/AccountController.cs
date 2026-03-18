using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using LocalList.API.NET.Shared.Data;

namespace LocalList.API.NET.Features.Account;

[ApiController]
[Route("account")]
[Authorize]
public class AccountController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<AccountController> _logger;

    public AccountController(LocalListDbContext db, ILogger<AccountController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAccount(CancellationToken ct)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out Guid userId))
            return Unauthorized(new { error = "Invalid token claims" });

        var user = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                id = u.Id,
                email = u.Email,
                name = u.Name,
                image = u.Image,
                tier = u.Tier,
                role = u.Role,
                city = u.City,
                createdAt = u.CreatedAt
            })
            .FirstOrDefaultAsync(ct);

        if (user == null)
            return NotFound(new { error = "User not found" });

        return Ok(new { user });
    }

    // Apple Guideline 5.1.1(v) - Account deletion
    [HttpDelete]
    public async Task<IActionResult> DeleteAccount(CancellationToken ct)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out Guid userId))
            return Unauthorized(new { error = "Invalid token claims" });

        var user = await _db.Users.FindAsync([userId], ct);
        if (user == null)
            return NotFound(new { error = "User not found" });

        // Nullify references in plans and places (no cascade on these FKs)
        var plans = await _db.Plans.Where(p => p.CreatedById == userId).ToListAsync(ct);
        foreach (var plan in plans) { plan.CreatedById = null; }

        var submittedPlaces = await _db.Places.Where(p => p.SubmittedById == userId).ToListAsync(ct);
        foreach (var place in submittedPlaces) { place.SubmittedById = null; }

        var reviewedPlaces = await _db.Places.Where(p => p.ReviewedById == userId).ToListAsync(ct);
        foreach (var place in reviewedPlaces) { place.ReviewedById = null; }

        // Delete user (RefreshTokens and FollowSessions will cascade automatically via DB constraints)
        _db.Users.Remove(user);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Account deleted: {UserId}", userId);
        return Ok(new { message = "Account deleted" });
    }
}
