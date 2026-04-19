using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace LocalList.API.NET.Features.Auth.Services;

public interface IJwtTokenService
{
    string SignAccessToken(Guid userId, string email, string tier);
}

public class JwtTokenService : IJwtTokenService
{
    public const string Issuer = "locallist-api";
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    private readonly SymmetricSecurityKey _signingKey;
    private readonly TimeProvider _clock;

    public JwtTokenService(IConfiguration configuration, TimeProvider clock)
    {
        var secret = Environment.GetEnvironmentVariable("JWT_SECRET")
                     ?? configuration["Jwt:Secret"]
                     ?? throw new InvalidOperationException(
                         "JWT_SECRET is not configured. Set the JWT_SECRET env var (>=32 bytes).");

        if (Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("JWT_SECRET must be at least 32 bytes for HS256.");

        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        _clock = clock;
    }

    public string SignAccessToken(Guid userId, string email, string tier)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: null,
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, email),
                new Claim("tier", tier),
                new Claim(JwtRegisteredClaimNames.Iat,
                    new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),
                    ClaimValueTypes.Integer64)
            },
            notBefore: now,
            expires: now.Add(AccessTokenLifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
