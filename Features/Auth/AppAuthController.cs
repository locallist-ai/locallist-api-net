using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Features.Auth.Services;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Auth;

/// <summary>
/// App-side auth (B2C). Issues HS256 JWTs against the local users table.
/// Admin uses Firebase + /auth/sync (see <see cref="AuthController"/>).
/// </summary>
[ApiController]
[Route("auth")]
[EnableRateLimiting("AuthLimit")]
public class AppAuthController : ControllerBase
{
    private const string AdminDomain = "@locallist.ai";

    private readonly LocalListDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenService _jwt;
    private readonly IRefreshTokenService _refresh;
    private readonly IAppleIdTokenValidator _apple;
    private readonly IGoogleIdTokenValidator _google;
    private readonly ILogger<AppAuthController> _logger;

    public AppAuthController(
        LocalListDbContext db,
        TimeProvider clock,
        IPasswordHasher hasher,
        IJwtTokenService jwt,
        IRefreshTokenService refresh,
        IAppleIdTokenValidator apple,
        IGoogleIdTokenValidator google,
        ILogger<AppAuthController> logger)
    {
        _db = db;
        _clock = clock;
        _hasher = hasher;
        _jwt = jwt;
        _refresh = refresh;
        _apple = apple;
        _google = google;
        _logger = logger;
    }

    [HttpPost("signin")]
    public async Task<IActionResult> Signin([FromBody] SigninRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(new { error = "Invalid request" });

        var claims = request.Provider switch
        {
            "apple" => await _apple.ValidateAsync(request.IdToken, ct),
            "google" => await _google.ValidateAsync(request.IdToken, ct),
            _ => null
        };

        if (claims is null) return Unauthorized(new { error = "Invalid ID token" });
        if (string.IsNullOrEmpty(claims.Email))
            return BadRequest(new { error = "Email not provided by identity provider" });

        var providerSub = claims.Sub;
        var user = request.Provider == "apple"
            ? await _db.Users.FirstOrDefaultAsync(
                u => u.AppleUserId == providerSub || u.Email == claims.Email, ct)
            : await _db.Users.FirstOrDefaultAsync(
                u => u.GoogleUserId == providerSub || u.Email == claims.Email, ct);

        if (user is null)
        {
            user = new User
            {
                Email = claims.Email,
                Name = request.Name ?? claims.Name,
                Image = claims.Picture,
                Role = ResolveRole(claims.Email)
            };
            if (request.Provider == "apple") user.AppleUserId = providerSub;
            else user.GoogleUserId = providerSub;
            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("New user via {Provider} OAuth", request.Provider);
        }
        else
        {
            var providerEmpty = request.Provider == "apple"
                ? string.IsNullOrEmpty(user.AppleUserId)
                : string.IsNullOrEmpty(user.GoogleUserId);
            if (providerEmpty)
            {
                if (request.Provider == "apple") user.AppleUserId = providerSub;
                else user.GoogleUserId = providerSub;
                user.UpdatedAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
            }
        }

        return Ok(await IssueTokensAsync(user, ct));
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(new { error = "Invalid request" });

        var existing = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email, ct);
        if (existing is not null) return Conflict(new { error = "Email already registered" });

        var user = new User
        {
            Email = request.Email,
            Name = request.Name,
            PasswordHash = _hasher.Hash(request.Password),
            Role = ResolveRole(request.Email)
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("New user via email/password registration");

        return Ok(await IssueTokensAsync(user, ct));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(new { error = "Invalid credentials" });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email, ct);
        // Generic message for both unknown email and wrong password (user enumeration defense)
        if (user is null) return Unauthorized(new { error = "Invalid credentials" });

        if (string.IsNullOrEmpty(user.PasswordHash))
            return Unauthorized(new { error = "Use Apple or Google to sign in" });

        if (!_hasher.Verify(request.Password, user.PasswordHash))
            return Unauthorized(new { error = "Invalid credentials" });

        return Ok(await IssueTokensAsync(user, ct));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(new { error = "Invalid request" });

        var rotated = await _refresh.RotateAsync(request.RefreshToken, ct);
        if (rotated is null) return Unauthorized(new { error = "Invalid or expired refresh token" });

        return Ok(new { accessToken = rotated.NewAccessToken, refreshToken = rotated.NewPlainToken });
    }

    private async Task<AppAuthResponse> IssueTokensAsync(User user, CancellationToken ct)
    {
        var access = _jwt.SignAccessToken(user.Id, user.Email, user.Tier);
        var issued = await _refresh.IssueAsync(user.Id, ct);
        return new AppAuthResponse(
            AccessToken: access,
            RefreshToken: issued.PlainToken,
            User: new AppAuthUser(user.Id, user.Email, user.Name, user.Image, user.Tier));
    }

    private static string ResolveRole(string email) =>
        email.EndsWith(AdminDomain, StringComparison.OrdinalIgnoreCase) ? "admin" : "user";
}
