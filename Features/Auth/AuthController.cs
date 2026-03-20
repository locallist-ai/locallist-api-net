using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Auth;

namespace LocalList.API.NET.Features.Auth;

[ApiController]
[Route("auth")]
[EnableRateLimiting("AuthLimit")]
public class AuthController : ControllerBase
{
    private const string AdminDomain = "@locallist.ai";

    private static string ResolveRole(string email) =>
        email.EndsWith(AdminDomain, StringComparison.OrdinalIgnoreCase) ? "admin" : "user";

    private readonly LocalListDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuthController> _logger;

    public AuthController(LocalListDbContext db, TimeProvider clock, ILogger<AuthController> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Syncs a Firebase-authenticated user with the local database.
    /// Creates a new user on first login, or links an existing user by email (migration).
    /// </summary>
    [HttpPost("sync")]
    [Authorize]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        var firebaseUid = User.GetFirebaseUid();
        var email = User.GetEmail();

        if (string.IsNullOrEmpty(firebaseUid) || string.IsNullOrEmpty(email))
            return BadRequest(new { error = "Token missing required claims (sub, email)" });

        // Look up by firebase_uid first, then by email (for migrating existing users)
        var user = await _db.Users.FirstOrDefaultAsync(u => u.FirebaseUid == firebaseUid, ct)
                   ?? await _db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user == null)
        {
            // New user — create
            user = new User
            {
                Email = email,
                FirebaseUid = firebaseUid,
                Role = ResolveRole(email)
            };
            _db.Users.Add(user);
            _logger.LogInformation("New user created via Firebase sync: {Email}", email);
        }
        else if (string.IsNullOrEmpty(user.FirebaseUid))
        {
            // Existing user without firebase_uid — link (migration)
            user.FirebaseUid = firebaseUid;
            user.Role = ResolveRole(user.Email);
            user.UpdatedAt = _clock.GetUtcNow();
            _logger.LogInformation("Linked Firebase UID to existing user {UserId}", user.Id);
        }
        else
        {
            // Existing Firebase user — sync role
            user.Role = ResolveRole(user.Email);
            user.UpdatedAt = _clock.GetUtcNow();
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            user = new
            {
                id = user.Id,
                email = user.Email,
                name = user.Name,
                image = user.Image,
                tier = user.Tier,
                role = user.Role
            }
        });
    }
}
