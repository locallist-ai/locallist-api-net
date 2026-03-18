using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Shared.Auth;

public class JwtTokenService
{
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _clock;

    public JwtTokenService(IConfiguration configuration, TimeProvider clock)
    {
        _configuration = configuration;
        _clock = clock;
    }

    public string GenerateAccessToken(User user)
    {
        var jwtSettings = _configuration.GetSection("Jwt");
        var secretKey = jwtSettings["Secret"];
        var issuer = jwtSettings["Issuer"];
        var audience = jwtSettings["Audience"];

        if (string.IsNullOrEmpty(secretKey))
            throw new Exception("JWT Secret not configured.");

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("tier", user.Tier),
            new Claim("role", user.Role)
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = _clock.GetUtcNow().AddMinutes(15).UtcDateTime,
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = credentials
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(tokenDescriptor);
        return handler.WriteToken(token);
    }

    public string GenerateRefreshToken()
    {
        var randomNumber = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        return Convert.ToBase64String(randomNumber);
    }

    public DateTimeOffset GetRefreshTokenExpiry()
    {
        return _clock.GetUtcNow().AddDays(30);
    }
}
