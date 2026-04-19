using System.Security.Cryptography;
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
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenService _jwt;
    private readonly TimeProvider _clock;

    public RefreshTokenService(
        LocalListDbContext db,
        IPasswordHasher hasher,
        IJwtTokenService jwt,
        TimeProvider clock)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt;
        _clock = clock;
    }

    public async Task<RefreshTokenIssue> IssueAsync(Guid userId, CancellationToken ct)
    {
        var plain = GenerateToken();
        var entity = new RefreshToken
        {
            UserId = userId,
            TokenHash = _hasher.Hash(plain),
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
        var candidates = await _db.RefreshTokens
            .Include(rt => rt.User)
            .Where(rt => rt.TokenPrefix == prefix)
            .ToListAsync(ct);

        foreach (var candidate in candidates)
        {
            if (!_hasher.Verify(plainToken, candidate.TokenHash)) continue;

            // Always delete the candidate (whether expired or not — single-use)
            _db.RefreshTokens.Remove(candidate);

            if (_clock.GetUtcNow() > candidate.ExpiresAt || candidate.User is null)
            {
                await _db.SaveChangesAsync(ct);
                return null;
            }

            // Issue new pair atomically
            var newPlain = GenerateToken();
            _db.RefreshTokens.Add(new RefreshToken
            {
                UserId = candidate.UserId,
                TokenHash = _hasher.Hash(newPlain),
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
}
