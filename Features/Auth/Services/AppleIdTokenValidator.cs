using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LocalList.API.NET.Features.Auth.Services;

public record OAuthClaims(string Sub, string? Email, string? Name, string? Picture);

public interface IAppleIdTokenValidator
{
    Task<OAuthClaims?> ValidateAsync(string idToken, CancellationToken ct);
}

public class AppleIdTokenValidator : IAppleIdTokenValidator
{
    private const string Issuer = "https://appleid.apple.com";
    private const string JwksUri = "https://appleid.apple.com/auth/keys";

    private readonly ConfigurationManager<OpenIdConnectConfiguration> _cfg;
    private readonly string _audience;
    private readonly ILogger<AppleIdTokenValidator> _logger;

    public AppleIdTokenValidator(IConfiguration configuration, ILogger<AppleIdTokenValidator> logger)
    {
        _audience = Environment.GetEnvironmentVariable("APPLE_BUNDLE_ID")
                    ?? configuration["Apple:BundleId"]
                    ?? throw new InvalidOperationException(
                        "APPLE_BUNDLE_ID is not configured.");

        _cfg = new ConfigurationManager<OpenIdConnectConfiguration>(
            JwksUri,
            new JwksRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });

        _logger = logger;
    }

    public async Task<OAuthClaims?> ValidateAsync(string idToken, CancellationToken ct)
    {
        try
        {
            var config = await _cfg.GetConfigurationAsync(ct);
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = Issuer,
                ValidateAudience = true,
                ValidAudience = _audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = config.SigningKeys
            };

            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(idToken, validationParameters, out _);
            var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrEmpty(sub)) return null;

            return new OAuthClaims(
                Sub: sub,
                Email: principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value,
                Name: null,
                Picture: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apple ID token validation failed");
            return null;
        }
    }
}
