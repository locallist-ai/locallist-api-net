using System.IdentityModel.Tokens.Jwt;
using System.Text;
using LocalList.API.NET.Features.Auth.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace LocalList.API.NET.Shared.Startup;

public static class AuthenticationExtensions
{
    /// <summary>
    /// Configures the multi-scheme JWT authentication used across the API.
    /// Two parallel schemes coexist: "Firebase" (RS256, used by /auth/sync + admin),
    /// "App" (HS256, used by the mobile app via /auth/signin|login|register|refresh).
    /// "Multi" is the policy scheme that picks one based on the token's `iss` claim.
    /// Also registers app auth services (HS256 issuance, password hashing, OAuth validation).
    /// </summary>
    public static IServiceCollection AddJwtAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        var firebaseProjectId =
            Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID") is { Length: > 0 } envVar ? envVar
            : configuration["Firebase:ProjectId"] is { Length: > 0 } cfgVar ? cfgVar
            : throw new InvalidOperationException("Firebase ProjectId is not configured. Set FIREBASE_PROJECT_ID env var.");

        const string FirebaseScheme = "Firebase";
        const string AppScheme = "App";
        const string MultiScheme = "Multi";

        services.AddAuthentication(MultiScheme)
            .AddJwtBearer(FirebaseScheme, options =>
            {
                options.IncludeErrorDetails = true;
                options.Authority = $"https://securetoken.google.com/{firebaseProjectId}";
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = $"https://securetoken.google.com/{firebaseProjectId}",
                    ValidateAudience = true,
                    ValidAudience = firebaseProjectId,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                };
                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                        logger.LogError(context.Exception, "JWT validation failed for Firebase token");
                        return Task.CompletedTask;
                    }
                };
            })
            .AddJwtBearer(AppScheme, options =>
            {
                var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
                                ?? configuration["Jwt:Secret"]
                                ?? throw new InvalidOperationException(
                                    "JWT_SECRET is not configured. Set the JWT_SECRET env var (>=32 bytes).");
                if (Encoding.UTF8.GetByteCount(jwtSecret) < 32)
                    throw new InvalidOperationException("JWT_SECRET must be at least 32 bytes for HS256.");

                options.IncludeErrorDetails = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = JwtTokenService.Issuer,
                    ValidateAudience = true,
                    ValidAudiences = new[] { JwtTokenService.Audience },
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
                };
            })
            .AddPolicyScheme(MultiScheme, "Firebase or App JWT", options =>
            {
                // Audit follow-up 2026-04-27 (C1): size cap antes de parsear bytes
                // attacker-controlled. JwtSecurityTokenHandler.ReadJwtToken hace
                // base64-decode del header+payload sin tope de tamaño — tokens de 1MB
                // se parseaban antes de rechazarse.
                const int MaxTokenLength = 4096;
                options.ForwardDefaultSelector = context =>
                {
                    var auth = context.Request.Headers["Authorization"].FirstOrDefault();
                    if (auth is null || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        return FirebaseScheme;
                    var token = auth["Bearer ".Length..].Trim();
                    if (token.Length > MaxTokenLength)
                    {
                        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                        logger.LogWarning(
                            "Bearer token of {Length} chars exceeded {Max} cap on {Path}; routing to {Scheme}",
                            token.Length, MaxTokenLength, context.Request.Path, FirebaseScheme);
                        return FirebaseScheme;
                    }
                    try
                    {
                        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
                        return jwt.Issuer == JwtTokenService.Issuer ? AppScheme : FirebaseScheme;
                    }
                    catch (Exception ex)
                    {
                        // Tokens malformados → FirebaseScheme. Log warn con request path
                        // para no enmascarar señal de App tokens corruptos.
                        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                        logger.LogWarning(ex,
                            "Failed to parse Bearer token on {Path}; routing to {Scheme}",
                            context.Request.Path, FirebaseScheme);
                        return FirebaseScheme;
                    }
                };
            });

        services.AddAuthorization();

        // App auth services (HS256 JWT issuance, password hashing, OAuth ID token validation)
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddSingleton<IAppleIdTokenValidator, AppleIdTokenValidator>();
        services.AddSingleton<IGoogleIdTokenValidator, GoogleIdTokenValidator>();

        return services;
    }
}
