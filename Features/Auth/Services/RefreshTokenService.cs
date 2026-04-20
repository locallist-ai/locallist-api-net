using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Auth.Services;

public record RefreshTokenIssue(string PlainToken, RefreshToken Stored);

public record RefreshTokenRotation(string NewPlainToken, string NewAccessToken);

public interface IRefreshTokenService
{
    Task<RefreshTokenIssue> IssueAsync(Guid userId, CancellationToken ct);
    Task<RefreshTokenRotation?> RotateAsync(string plainToken, CancellationToken ct);
}

public class RefreshTokenService : IRefreshTokenService
{
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);
    private const int PrefixLength = 16;

    private readonly LocalListDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly TimeProvider _clock;

    public RefreshTokenService(
        LocalListDbContext db,
        IJwtTokenService jwt,
        TimeProvider clock)
    {
        _db = db;
        _jwt = jwt;
        _clock = clock;
    }

    public async Task<RefreshTokenIssue> IssueAsync(Guid userId, CancellationToken ct)
    {
        var plain = GenerateToken();
        var entity = new RefreshToken
        {
            UserId = userId,
            TokenHash = HashToken(plain),
            TokenPrefix = plain[..PrefixLength],
            ExpiresAt = _clock.GetUtcNow().Add(RefreshLifetime),
            CreatedAt = _clock.GetUtcNow()
        };
        _db.RefreshTokens.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new RefreshTokenIssue(plain, entity);
    }

    public async Task<RefreshTokenRotation?> RotateAsync(string plainToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(plainToken) || plainToken.Length < PrefixLength)
            return null;

        var prefix = plainToken[..PrefixLength];
        var incomingHash = HashToken(plainToken);

        var candidates = await _db.RefreshTokens
            .Include(rt => rt.User)
            .Where(rt => rt.TokenPrefix == prefix)
            .ToListAsync(ct);

        foreach (var candidate in candidates)
        {
            if (!FixedTimeEquals(candidate.TokenHash, incomingHash)) continue;

            // Single-use rotation: always remove the matched candidate
            _db.RefreshTokens.Remove(candidate);

            if (_clock.GetUtcNow() > candidate.ExpiresAt || candidate.User is null)
            {
                await _db.SaveChangesAsync(ct);
                return null;
            }

            var newPlain = GenerateToken();
            _db.RefreshTokens.Add(new RefreshToken
            {
                UserId = candidate.UserId,
                TokenHash = HashToken(newPlain),
                TokenPrefix = newPlain[..PrefixLength],
                ExpiresAt = _clock.GetUtcNow().Add(RefreshLifetime),
                CreatedAt = _clock.GetUtcNow()
            });
            var accessToken = _jwt.SignAccessToken(candidate.UserId, candidate.User.Email, candidate.User.Tier);
            await _db.SaveChangesAsync(ct);
            return new RefreshTokenRotation(newPlain, accessToken);
        }

        return null;
    }

    private static string GenerateToken()
    {
        Span<byte> buffer = stackalloc byte[64];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }

    // Refresh tokens use SHA-256, NOT bcrypt: bcrypt's 72-byte input limit
    // would reject our 128-char hex tokens, and bcrypt's slowness is unwanted
    // here (tokens are high-entropy random, no brute-force risk; only constant-
    // time compare is needed).
    private static string HashToken(string plainToken)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(plainToken), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string storedHashHex, string incomingHashHex)
    {
        if (storedHashHex.Length != incomingHashHex.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHashHex),
            Encoding.ASCII.GetBytes(incomingHashHex));
    }
}
