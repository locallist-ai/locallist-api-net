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

    // Grace window for a re-presented, already-rotated (not revoked) token. Within it,
    // the presentation is treated as a benign lost-response retry (client's rotation
    // response never arrived — flaky Wi-Fi / backgrounded app — so it still holds the
    // old token) and is answered with a FRESH pair, NOT a family revocation. Past it, a
    // replay of a long-spent token is treated as exfiltration → family revocation. This
    // trades catching a replay within the first {grace}s for availability on flaky
    // networks; a genuine exfil-then-replay lands well outside the window.
    private static readonly TimeSpan ReuseGrace = TimeSpan.FromSeconds(60);

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
        var now = _clock.GetUtcNow();

        var candidates = await _db.RefreshTokens
            .Include(rt => rt.User)
            .Where(rt => rt.TokenPrefix == prefix)
            .ToListAsync(ct);

        foreach (var candidate in candidates)
        {
            if (!FixedTimeEquals(candidate.TokenHash, incomingHash)) continue;

            // (1) EXPIRY FIRST. An expired token — active, rotated, or revoked — simply
            //     fails; it must NEVER trigger family revocation (an expired-but-unpruned
            //     rotated token replayed just fails on expiry, it is not "reuse").
            if (now > candidate.ExpiresAt || candidate.User is null)
                return null;

            // (2) ALREADY REVOKED (family revocation ran) → permanently dead. Quiet 401:
            //     no mint, no re-revoke. Replaying a revoked token must not resurrect the
            //     session, and must not fall into the grace window below.
            if (candidate.RevokedAt is not null)
                return null;

            // (3) ROTATED but not revoked → distinguish a benign lost-response
            //     re-presentation from a genuine after-the-fact replay. NEITHER outcome
            //     is a 401-only dead end for a legit client: benign cases hand back a
            //     fresh pair so the session survives.
            if (candidate.RotatedAt is not null)
            {
                // (3a) FAST PATH — within grace: assume benign (lost-response retry or a
                //      rapid legit double-submit) and mint a fresh pair, no chain lookup,
                //      no revoke. Repeated retries in-window stay graceful.
                if (now - candidate.RotatedAt.Value <= ReuseGrace)
                    return await MintFreshPairAsync(candidate, now, ct);

                // (3b) PAST GRACE — revoke ONLY on evidence the chain advanced: the direct
                //      successor was itself CONSUMED (rotated or revoked), i.e. a second
                //      party used it → genuine exfil-then-replay (RFC 6819 discriminator).
                //      If the successor was never consumed (or there is none), this is a
                //      benign LATE lost-response retry — the client was suspended past the
                //      window before the successor ever arrived → recover gracefully. The
                //      recovery mint deliberately does NOT advance the successor, so any
                //      number of late retries stay benign at any delay (retry-storm safe).
                var successorConsumed = candidate.ReplacedById is Guid successorId
                    && await _db.RefreshTokens.AnyAsync(
                        rt => rt.Id == successorId && (rt.RotatedAt != null || rt.RevokedAt != null), ct);

                if (successorConsumed)
                {
                    // Generic 401: never leak to the caller that reuse was detected.
                    await RevokeFamilyAsync(candidate.UserId, ct);
                    _logger.LogWarning(
                        "Refresh token reuse detected for user {UserId}; revoked all refresh tokens for that user",
                        candidate.UserId);
                    return null;
                }

                return await MintFreshPairAsync(candidate, now, ct);
            }

            // (4) ACTIVE token → atomic single-use rotation (concurrency-safe).
            return await TryRotateActiveAsync(candidate, now, ct);
        }

        // No hash match: the token never existed (or was already pruned). NOT reuse —
        // a random/garbage token must never nuke a user's session.
        return null;
    }

    // Atomically CLAIM the single-use rotation of an active token, then mint the new
    // pair. The `WHERE RotatedAt == null && RevokedAt == null` guard is the ONE
    // serialization point: of two concurrent refreshes of the SAME valid token, exactly
    // one UPDATE affects a row. The loser sees 0 rows and bows out WITHOUT minting a
    // second valid token and WITHOUT revoking — a concurrent double-submit is legit, not
    // reuse; the winner already returned a working pair.
    private async Task<RefreshTokenRotation?> TryRotateActiveAsync(
        RefreshToken candidate, DateTimeOffset now, CancellationToken ct)
    {
        var claimed = await _db.RefreshTokens
            .Where(rt => rt.Id == candidate.Id && rt.RotatedAt == null && rt.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(rt => rt.RotatedAt, now), ct);

        if (claimed == 0) return null;

        var (rotation, newTokenId) = await MintPairAsync(candidate, now, ct);

        // Link the chain A→B (legit single-use rotation only). This is what lets the
        // past-grace branch tell a benign late retry (successor never consumed) from a
        // genuine replay (successor consumed). ExecuteUpdate keeps it off the tracker.
        await _db.RefreshTokens
            .Where(rt => rt.Id == candidate.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(rt => rt.ReplacedById, newTokenId), ct);

        return rotation;
    }

    // Grace / benign lost-response recovery: mint a fresh pair WITHOUT consuming or
    // advancing the source's successor (so unlimited late retries stay benign).
    private async Task<RefreshTokenRotation> MintFreshPairAsync(
        RefreshToken source, DateTimeOffset now, CancellationToken ct)
    {
        var (rotation, _) = await MintPairAsync(source, now, ct);
        return rotation;
    }

    // Mint a brand-new refresh/access pair for the token's user WITHOUT consuming any
    // existing row. Returns the new refresh token's Id so the caller can chain-link it
    // (legit rotation) or discard it (benign recovery).
    private async Task<(RefreshTokenRotation rotation, Guid newTokenId)> MintPairAsync(
        RefreshToken source, DateTimeOffset now, CancellationToken ct)
    {
        var newPlain = GenerateToken();
        var entity = new RefreshToken
        {
            UserId = source.UserId,
            TokenHash = HashToken(newPlain),
            TokenPrefix = newPlain[..PrefixLength],
            ExpiresAt = now.Add(RefreshLifetime),
            CreatedAt = now
        };
        _db.RefreshTokens.Add(entity);
        var accessToken = _jwt.SignAccessToken(source.UserId, source.User!.Email, source.User.Tier);
        await _db.SaveChangesAsync(ct);
        return (new RefreshTokenRotation(newPlain, accessToken), entity.Id);
    }

    // Token-family revocation: kill every not-yet-revoked refresh token for the user —
    // active AND still-grace-eligible rotated rows alike (so a just-revoked token cannot
    // slip back in through the grace window). Bypasses the change tracker (ExecuteUpdate)
    // so it never conflicts with the tracked candidate. Wrapped in an explicit
    // transaction so the bulk kill commits atomically as one unit.
    private async Task RevokeFamilyAsync(Guid userId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(rt => rt.RevokedAt, now), ct);
        await tx.CommitAsync(ct);
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
