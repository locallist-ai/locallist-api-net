using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LocalList.API.NET.Features.Auth.Services;

public interface IGoogleIdTokenValidator
{
    Task<OAuthClaims?> ValidateAsync(string idToken, CancellationToken ct);
}

public class GoogleIdTokenValidator : IGoogleIdTokenValidator
{
    private static readonly string[] ValidIssuers =
        { "https://accounts.google.com", "accounts.google.com" };
    private const string JwksUri = "https://www.googleapis.com/oauth2/v3/certs";

    private readonly ConfigurationManager<OpenIdConnectConfiguration> _cfg;
    private readonly string _audience;
    private readonly ILogger<GoogleIdTokenValidator> _logger;

    public GoogleIdTokenValidator(IConfiguration configuration, ILogger<GoogleIdTokenValidator> logger)
    {
        _audience = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID")
                    ?? configuration["Google:ClientId"]
                    ?? throw new InvalidOperationException(
                        "GOOGLE_CLIENT_ID is not configured.");

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
                ValidIssuers = ValidIssuers,
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
                Name: principal.FindFirst("name")?.Value,
                Picture: principal.FindFirst("picture")?.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google ID token validation failed");
            return null;
        }
    }
}
