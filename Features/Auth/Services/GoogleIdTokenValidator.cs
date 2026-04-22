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
    private readonly string[] _audiences;
    private readonly ILogger<GoogleIdTokenValidator> _logger;

    public GoogleIdTokenValidator(IConfiguration configuration, ILogger<GoogleIdTokenValidator> logger)
    {
        // Google emite ID tokens con `aud` = client ID de la plataforma que inició el flow
        // (Web vs iOS vs Android). expo-auth-session en iOS usa el iOS client ID, en Android el
        // Android, y en web el Web. Aceptamos los tres para no romper según plataforma.
        var webClientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID")
                          ?? configuration["Google:ClientId"]
                          ?? throw new InvalidOperationException(
                              "GOOGLE_CLIENT_ID is not configured.");

        var list = new List<string> { webClientId };

        var iosClientId = Environment.GetEnvironmentVariable("GOOGLE_IOS_CLIENT_ID")
                          ?? configuration["Google:IosClientId"];
        if (!string.IsNullOrWhiteSpace(iosClientId)) list.Add(iosClientId);

        var androidClientId = Environment.GetEnvironmentVariable("GOOGLE_ANDROID_CLIENT_ID")
                              ?? configuration["Google:AndroidClientId"];
        if (!string.IsNullOrWhiteSpace(androidClientId)) list.Add(androidClientId);

        _audiences = list.ToArray();

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
                ValidAudiences = _audiences,
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
