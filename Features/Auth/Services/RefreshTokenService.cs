using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<RefreshTokenService> _logger;

    public RefreshTokenService(
        LocalListDbContext db,
        IJwtTokenService jwt,
        TimeProvider clock,
        ILogger<RefreshTokenService> logger)
    {
        _db = db;
        _jwt = jwt;
        _clock = clock;
        _logger = logger;
    }

    public async Task<RefreshTokenIssue> IssueAsync(Guid userId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        // Retention/cleanup: prune this user's tokens (active OR rotated) past the
        // 30d refresh window. A rotated token past its original expiry can no longer
        // be usefully replayed (an exfiltrated original would fail the expiry check
        // anyway), so reuse detection loses nothing while rows stay bounded.
        await _db.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.ExpiresAt < now)
            .ExecuteDeleteAsync(ct);

        var plain = GenerateToken();
        var entity = new RefreshToken
        {
            UserId = userId,
            TokenHash = HashToken(plain),
            TokenPrefix = plain[..PrefixLength],
            ExpiresAt = now.Add(RefreshLifetime),
            CreatedAt = now
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

            // REUSE DETECTION: the presented token matches a KNOWN refresh token
            // that was already rotated/invalidated. A spent token being replayed
            // means it was captured and reused after the legit rotation → revoke
            // the whole token family (all of this user's active tokens) and alarm.
            // The response is the SAME generic 401 as any other failure — never
            // leak to the caller that reuse was detected.
            if (candidate.RotatedAt is not null)
            {
                await RevokeAllActiveForUserAsync(candidate.UserId, ct);
                _logger.LogWarning(
                    "Refresh token reuse detected for user {UserId}; revoked all active refresh tokens for that user",
                    candidate.UserId);
                return null;
            }

            // Single-use rotation: mark the matched candidate as rotated (RETAIN
            // it — do not hard-delete — so a later replay is detectable as reuse).
            candidate.RotatedAt = _clock.GetUtcNow();

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

        // No hash match: the token never existed (or was already pruned). This is
        // NOT reuse — a random/garbage token must NOT nuke a user's session.
        return null;
    }

    // Token-family revocation: invalidate every ACTIVE (not-yet-rotated) refresh
    // token for the user in a single atomic UPDATE. Rows already rotated keep their
    // original RotatedAt. Bypasses the change tracker (ExecuteUpdate) so it does not
    // conflict with the reused candidate we did not modify in this branch.
    private async Task RevokeAllActiveForUserAsync(Guid userId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await _db.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RotatedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(rt => rt.RotatedAt, now), ct);
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
