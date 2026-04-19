using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using LocalList.API.NET.Features.Auth.Services;
using LocalList.API.NET.Shared.Data;

namespace LocalList.API.Tests;

public class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TestFirebaseProjectId = "test-firebase-project";
    public const string TestJwtSecret = "test-jwt-secret-must-be-at-least-32-bytes-long-aaaaaaaaaaaa";
    private const string TestAppleBundleId = "test.bundle.id";
    private const string TestGoogleClientId = "test-google-client-id.apps.googleusercontent.com";
    private static readonly RSA _testRsa = RSA.Create(2048);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public FakeTimeProvider FakeTime { get; } = new(DateTimeOffset.UtcNow);
    public FakeAppleIdTokenValidator FakeApple { get; } = new();
    public FakeGoogleIdTokenValidator FakeGoogle { get; } = new();

    private bool _dbCreated;

    static ApiFixture()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", "");
        Environment.SetEnvironmentVariable("FIREBASE_PROJECT_ID", TestFirebaseProjectId);
        Environment.SetEnvironmentVariable("JWT_SECRET", TestJwtSecret);
        Environment.SetEnvironmentVariable("APPLE_BUNDLE_ID", TestAppleBundleId);
        Environment.SetEnvironmentVariable("GOOGLE_CLIENT_ID", TestGoogleClientId);
    }

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
    }

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureTestServices(services =>
        {
            // Remove the production DbContext registration (if any)
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<LocalListDbContext>));
            if (descriptor is not null) services.Remove(descriptor);

            services.AddDbContext<LocalListDbContext>(options =>
                options.UseNpgsql(_postgres.GetConnectionString()));

            // Replace TimeProvider.System with FakeTimeProvider
            var timeDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(TimeProvider));
            if (timeDescriptor is not null) services.Remove(timeDescriptor);

            services.AddSingleton<TimeProvider>(FakeTime);

            // Override Firebase scheme to use test RSA key instead of real Firebase JWKS
            services.PostConfigure<JwtBearerOptions>("Firebase", options =>
            {
                options.Authority = null;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = $"https://securetoken.google.com/{TestFirebaseProjectId}",
                    ValidateAudience = true,
                    ValidAudience = TestFirebaseProjectId,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new RsaSecurityKey(_testRsa)
                };
            });

            // Replace Apple/Google ID-token validators with in-process fakes
            // (real ones hit Apple/Google JWKS endpoints — not viable in tests).
            var appleDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IAppleIdTokenValidator));
            if (appleDescriptor is not null) services.Remove(appleDescriptor);
            services.AddSingleton<IAppleIdTokenValidator>(FakeApple);

            var googleDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IGoogleIdTokenValidator));
            if (googleDescriptor is not null) services.Remove(googleDescriptor);
            services.AddSingleton<IGoogleIdTokenValidator>(FakeGoogle);

            // Disable rate limiting in tests
            var rateLimiterDescriptors = services
                .Where(d => d.ServiceType == typeof(IConfigureOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>))
                .ToList();
            foreach (var d in rateLimiterDescriptors) services.Remove(d);

            services.AddRateLimiter(options =>
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<Microsoft.AspNetCore.Http.HttpContext, string>(
                    _ => RateLimitPartition.GetNoLimiter("test"));
                options.AddPolicy("AuthLimit", context =>
                    RateLimitPartition.GetNoLimiter(string.Empty));
                options.AddPolicy("BuilderLimit", context =>
                    RateLimitPartition.GetNoLimiter(string.Empty));
                options.AddPolicy("AdminLimit", context =>
                    RateLimitPartition.GetNoLimiter(string.Empty));
                options.AddPolicy("WaitlistLimit", context =>
                    RateLimitPartition.GetNoLimiter(string.Empty));
            });
        });
    }

    private void EnsureDb()
    {
        if (_dbCreated) return;
        _dbCreated = true;
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalListDbContext>();
        db.Database.Migrate();
    }

    public new HttpClient CreateClient()
    {
        var client = base.CreateClient();
        EnsureDb();
        return client;
    }

    /// <summary>
    /// Creates a Firebase-style RS256 JWT for testing.
    /// </summary>
    public string CreateToken(string firebaseUid, string email = "test@test.com")
    {
        var key = new RsaSecurityKey(_testRsa);
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, firebaseUid),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim("email_verified", "true"),
            new Claim("user_id", firebaseUid)
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = FakeTime.GetUtcNow().AddHours(1).UtcDateTime,
            Issuer = $"https://securetoken.google.com/{TestFirebaseProjectId}",
            Audience = TestFirebaseProjectId,
            SigningCredentials = credentials
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(tokenDescriptor);
        return handler.WriteToken(token);
    }

    public HttpClient CreateAuthenticatedClient(Guid userId, string firebaseUid, string email = "test@test.com")
    {
        var client = CreateClient();
        var token = CreateToken(firebaseUid, email);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Convenience overload: seeds a user with the given ID and firebase_uid, returns an authenticated client.
    /// </summary>
    public async Task<HttpClient> CreateAuthenticatedClientWithUser(
        Guid userId, string firebaseUid, string email = "test@test.com", string role = "user")
    {
        var db = GetDbContext();
        var existing = await db.Users.FindAsync(userId);
        if (existing == null)
        {
            db.Users.Add(new LocalList.API.NET.Shared.Data.Entities.User
            {
                Id = userId,
                Email = email,
                FirebaseUid = firebaseUid,
                Role = role
            });
            await db.SaveChangesAsync();
        }
        return CreateAuthenticatedClient(userId, firebaseUid, email);
    }

    public LocalListDbContext GetDbContext()
    {
        EnsureDb();
        var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<LocalListDbContext>();
    }

    /// <summary>
    /// Creates an HS256 JWT signed with the test JWT_SECRET — mirrors what
    /// <see cref="JwtTokenService"/> emits for the real app flow.
    /// </summary>
    public string CreateAppToken(Guid userId, string email, string tier = "free")
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = FakeTime.GetUtcNow().UtcDateTime;
        var token = new JwtSecurityToken(
            issuer: JwtTokenService.Issuer,
            audience: null,
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, email),
                new Claim("tier", tier)
            },
            notBefore: now,
            expires: now.AddMinutes(15),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public class FakeAppleIdTokenValidator : IAppleIdTokenValidator
{
    public Dictionary<string, OAuthClaims> Tokens { get; } = new();

    public Task<OAuthClaims?> ValidateAsync(string idToken, CancellationToken ct) =>
        Task.FromResult(Tokens.TryGetValue(idToken, out var claims) ? claims : null);
}

public class FakeGoogleIdTokenValidator : IGoogleIdTokenValidator
{
    public Dictionary<string, OAuthClaims> Tokens { get; } = new();

    public Task<OAuthClaims?> ValidateAsync(string idToken, CancellationToken ct) =>
        Task.FromResult(Tokens.TryGetValue(idToken, out var claims) ? claims : null);
}
