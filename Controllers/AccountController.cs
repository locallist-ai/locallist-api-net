using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using LocalList.API.NET.Data;

namespace LocalList.API.NET.Controllers;

[ApiController]
[Route("account")]
[Authorize] // Requires a valid JWT token
public class AccountController : ControllerBase
{
    private readonly LocalListDbContext _db;

    public AccountController(LocalListDbContext db)
    {
        _db = db;
    }

    // ─── GET /account ────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> GetAccount()
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
                city = u.City,
                createdAt = u.CreatedAt
            })
            .FirstOrDefaultAsync();

        if (user == null)
            return NotFound(new { error = "User not found" });

        return Ok(new { user });
    }

    // ─── DELETE /account ─────────────────────────────────────
    // Apple Guideline 5.1.1(v) - Account deletion
    [HttpDelete]
    public async Task<IActionResult> DeleteAccount()
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out Guid userId))
            return Unauthorized(new { error = "Invalid token claims" });

        var user = await _db.Users.FindAsync(userId);
        if (user == null)
            return NotFound(new { error = "User not found" });

        // Nullify references in plans and places (no cascade on these FKs)
        var plans = await _db.Plans.Where(p => p.CreatedById == userId).ToListAsync();
        foreach (var plan in plans) { plan.CreatedById = null; }

        var submittedPlaces = await _db.Places.Where(p => p.SubmittedById == userId).ToListAsync();
        foreach (var place in submittedPlaces) { place.SubmittedById = null; }

        var reviewedPlaces = await _db.Places.Where(p => p.ReviewedById == userId).ToListAsync();
        foreach (var place in reviewedPlaces) { place.ReviewedById = null; }

        // Delete user (RefreshTokens and FollowSessions will cascade automatically via DB constraints)
        _db.Users.Remove(user);
        
        await _db.SaveChangesAsync();

        return Ok(new { message = "Account deleted" });
    }
}
