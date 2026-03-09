using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Auth;
using Google.Apis.Auth;
using BCrypt.Net;

namespace LocalList.API.NET.Features.Auth;

[ApiController]
[Route("auth")]
[EnableRateLimiting("AuthLimit")]
public class AuthController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly JwtTokenService _jwtTokenService;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuthController> _logger;

    public AuthController(LocalListDbContext db, JwtTokenService jwtTokenService, TimeProvider clock, ILogger<AuthController> logger)
    {
        _db = db;
        _jwtTokenService = jwtTokenService;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Authenticates a user with email + password. No auth required.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> EmailLogin([FromBody] LoginRequest request, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email, ct);

        if (user == null)
            return Unauthorized(new { error = "Invalid credentials" });

        if (string.IsNullOrEmpty(user.PasswordHash))
            return Unauthorized(new { error = "Use Apple or Google to sign in" });

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            _logger.LogWarning("Failed login attempt for {Email}", request.Email);
            return Unauthorized(new { error = "Invalid credentials" });
        }

        _logger.LogInformation("User {UserId} logged in via {Method}", user.Id, "password");
        return await GenerateTokensResponse(user, ct);
    }

    /// <summary>Registers a new user with email + password. Returns generic error on duplicate to prevent email enumeration.</summary>
    [HttpPost("register")]
    public async Task<IActionResult> EmailRegister([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var existingUser = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email, ct);

        if (existingUser != null)
            return BadRequest(new { error = "Registration failed. Please check your details or try logging in." }); // M2 Fix: Generic error prevents email enumeration

        var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);

        var newUser = new User
        {
            Email = request.Email,
            Name = request.Name,
            PasswordHash = passwordHash
        };

        _db.Users.Add(newUser);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("New user registered: {UserId}", newUser.Id);
        return await GenerateTokensResponse(user: newUser, ct);
    }

    /// <summary>OAuth sign-in (Apple/Google). Creates user on first login or links provider to existing account. Apple returns 501 (not yet implemented).</summary>
    [HttpPost("signin")]
    public async Task<IActionResult> OAuthSignIn([FromBody] OAuthRequest request, CancellationToken ct)
    {
        string? providerUserId;
        string? email;
        string? userName = request.Name;
        string? image = null;

        try
        {
            if (request.Provider == "apple")
            {
                // C1 Fix: Apple OAuth Mock is strictly disabled. Will return 501 until native JWT verification is built.
                return StatusCode(501, new { error = "Apple Sign-In is not fully implemented yet on the backend." });
            }
            else if (request.Provider == "google")
            {
                var payload = await GoogleJsonWebSignature.ValidateAsync(request.IdToken);
                providerUserId = payload.Subject;
                email = payload.Email;
                userName = userName ?? payload.Name;
                image = payload.Picture;
            }
            else
            {
                // M6 Fix: Whitelist supported OAuth providers to prevent catch-all impersonation
                return BadRequest(new { error = "Unsupported OAuth provider" });
            }
        }
        catch
        {
            return Unauthorized(new { error = "Invalid ID token" });
        }

        if (string.IsNullOrEmpty(email))
            return BadRequest(new { error = "Email not provided by identity provider" });

        var user = await _db.Users.FirstOrDefaultAsync(u =>
            (request.Provider == "apple" && u.AppleUserId == providerUserId) ||
            (request.Provider == "google" && u.GoogleUserId == providerUserId) ||
            u.Email == email, ct);

        if (user == null)
        {
            user = new User
            {
                Email = email,
                Name = userName,
                Image = image
            };

            if (request.Provider == "apple") user.AppleUserId = providerUserId;
            else user.GoogleUserId = providerUserId;

            _db.Users.Add(user);
        }
        else
        {
            if (request.Provider == "apple" && user.AppleUserId == null)
                user.AppleUserId = providerUserId;
            else if (request.Provider == "google" && user.GoogleUserId == null)
                user.GoogleUserId = providerUserId;
            user.UpdatedAt = _clock.GetUtcNow();
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("User {UserId} signed in via {Provider}", user.Id, request.Provider);
        return await GenerateTokensResponse(user, ct);
    }

    /// <summary>Exchanges a refresh token for a new access + refresh pair. Enforces token rotation (old token is consumed). Opportunistically cleans up expired tokens.</summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.RefreshToken))
            return BadRequest(new { error = "Invalid request" });

        // M3 Fix: Clean up expired tokens periodically during refresh attempts
        var expiredTokens = await _db.RefreshTokens.Where(rt => rt.ExpiresAt < _clock.GetUtcNow()).Take(20).ToListAsync(ct);
        if (expiredTokens.Any())
        {
            _db.RefreshTokens.RemoveRange(expiredTokens);
            await _db.SaveChangesAsync(ct);
        }

        var tokenPrefix = request.RefreshToken.Substring(0, 16);

        var refreshTokens = await _db.RefreshTokens
            .Include(rt => rt.User)
            .Where(rt => rt.TokenPrefix == tokenPrefix)
            .ToListAsync(ct);

        foreach (var tokenRecord in refreshTokens)
        {
            if (BCrypt.Net.BCrypt.Verify(request.RefreshToken, tokenRecord.TokenHash))
            {
                if (_clock.GetUtcNow() > tokenRecord.ExpiresAt)
                {
                    _db.RefreshTokens.Remove(tokenRecord);
                    await _db.SaveChangesAsync(ct);
                    return Unauthorized(new { error = "Refresh token expired" });
                }

                _db.RefreshTokens.Remove(tokenRecord); // Enforce rotation
                _logger.LogInformation("Token refreshed for user {UserId}", tokenRecord.User!.Id);
                return await GenerateTokensResponse(tokenRecord.User!, ct);
            }
        }

        _logger.LogWarning("Token refresh failed: invalid or expired token");
        return Unauthorized(new { error = "Invalid refresh token" });
    }

    private async Task<IActionResult> GenerateTokensResponse(User user, CancellationToken ct = default)
    {
        var accessToken = _jwtTokenService.GenerateAccessToken(user);
        var rawRefreshToken = _jwtTokenService.GenerateRefreshToken();

        var tokenHash = BCrypt.Net.BCrypt.HashPassword(rawRefreshToken);
        var tokenPrefix = rawRefreshToken.Substring(0, 16);

        var tokenEntity = new Shared.Data.Entities.RefreshToken
        {
            UserId = user.Id,
            TokenHash = tokenHash,
            TokenPrefix = tokenPrefix,
            ExpiresAt = _jwtTokenService.GetRefreshTokenExpiry()
        };

        _db.RefreshTokens.Add(tokenEntity);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            accessToken,
            refreshToken = rawRefreshToken,
            user = new
            {
                id = user.Id,
                email = user.Email,
                name = user.Name,
                image = user.Image,
                tier = user.Tier
            }
        });
    }
}
