using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using LocalList.API.NET.Data;
using LocalList.API.NET.Data.Models;
using LocalList.API.NET.Services;
using Google.Apis.Auth;
using BCrypt.Net;

namespace LocalList.API.NET.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly JwtTokenService _jwtTokenService;

    public AuthController(LocalListDbContext db, JwtTokenService jwtTokenService)
    {
        _db = db;
        _jwtTokenService = jwtTokenService;
    }

    [HttpPost("login")]
    public async Task<IActionResult> EmailLogin([FromBody] LoginRequest request)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email);

        if (user == null)
            return Unauthorized(new { error = "Invalid credentials" });

        if (string.IsNullOrEmpty(user.PasswordHash))
            return Unauthorized(new { error = "Use Apple or Google to sign in" });

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            return Unauthorized(new { error = "Invalid credentials" });

        return await GenerateTokensResponse(user);
    }

    [HttpPost("register")]
    public async Task<IActionResult> EmailRegister([FromBody] RegisterRequest request)
    {
        var existingUser = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
        
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
        await _db.SaveChangesAsync();

        return await GenerateTokensResponse(newUser);
    }

    [HttpPost("signin")]
    public async Task<IActionResult> OAuthSignIn([FromBody] OAuthRequest request)
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
            u.Email == email);

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
            user.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync();
        return await GenerateTokensResponse(user);
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshRequest request)
    {
        if (string.IsNullOrEmpty(request.RefreshToken))
            return BadRequest(new { error = "Invalid request" });

        // M3 Fix: Clean up expired tokens periodically during refresh attempts
        var expiredTokens = await _db.RefreshTokens.Where(rt => rt.ExpiresAt < DateTimeOffset.UtcNow).Take(20).ToListAsync();
        if (expiredTokens.Any())
        {
            _db.RefreshTokens.RemoveRange(expiredTokens);
            await _db.SaveChangesAsync();
        }

        var tokenPrefix = request.RefreshToken.Substring(0, 16);
        
        var refreshTokens = await _db.RefreshTokens
            .Include(rt => rt.User)
            .Where(rt => rt.TokenPrefix == tokenPrefix)
            .ToListAsync();

        foreach (var tokenRecord in refreshTokens)
        {
            if (BCrypt.Net.BCrypt.Verify(request.RefreshToken, tokenRecord.TokenHash))
            {
                if (DateTimeOffset.UtcNow > tokenRecord.ExpiresAt)
                {
                    _db.RefreshTokens.Remove(tokenRecord);
                    await _db.SaveChangesAsync();
                    return Unauthorized(new { error = "Refresh token expired" });
                }

                _db.RefreshTokens.Remove(tokenRecord); // Enforce rotation
                return await GenerateTokensResponse(tokenRecord.User!);
            }
        }

        return Unauthorized(new { error = "Invalid refresh token" });
    }

    private async Task<IActionResult> GenerateTokensResponse(User user)
    {
        var accessToken = _jwtTokenService.GenerateAccessToken(user);
        var rawRefreshToken = _jwtTokenService.GenerateRefreshToken();
        
        var tokenHash = BCrypt.Net.BCrypt.HashPassword(rawRefreshToken);
        var tokenPrefix = rawRefreshToken.Substring(0, 16);

        var tokenEntity = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = tokenHash,
            TokenPrefix = tokenPrefix,
            ExpiresAt = _jwtTokenService.GetRefreshTokenExpiry()
        };

        _db.RefreshTokens.Add(tokenEntity);
        await _db.SaveChangesAsync();

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

public class LoginRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(100)]
    public string Password { get; set; } = string.Empty;
}

public class RegisterRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(100)]
    public string Password { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Name { get; set; }
}

public class OAuthRequest
{
    [Required]
    public string Provider { get; set; } = string.Empty;
    
    [Required]
    public string IdToken { get; set; } = string.Empty;
    
    [MaxLength(255)]
    public string? Name { get; set; }
}

public class RefreshRequest
{
    [Required]
    public string RefreshToken { get; set; } = string.Empty;
}
