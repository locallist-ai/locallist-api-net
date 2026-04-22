using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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

            // JwtSecurityTokenHandler tiene un legacy "inbound claim type map" que
            // reescribe los nombres cortos JWT (sub, email) a URIs WS-Fed
            // (nameidentifier, emailaddress). Lo desactivamos para que
            // FindFirst("sub") funcione contra los claims crudos del token.
            // Mismo patrón aplicado en GoogleIdTokenValidator (PR #40) — sin esto,
            // Apple Sign-In desde iOS devolvía 401 silencioso en producción.
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(idToken, validationParameters, out _);

            // Defensive lookup: si otro caller reintroduce el mapping, caemos a
            // ClaimTypes.NameIdentifier / ClaimTypes.Email.
            var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                      ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(sub))
            {
                _logger.LogWarning(
                    "Apple ID token validated but sub claim is empty. Claim types present: {Types}",
                    string.Join(",", principal.Claims.Select(c => c.Type)));
                return null;
            }

            var email = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
                        ?? principal.FindFirst(ClaimTypes.Email)?.Value;

            return new OAuthClaims(
                Sub: sub,
                Email: email,
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
