using System.Text.Json.Serialization;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Features.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog(configuration => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// A5: Limit Kestrel MaxRequestBodySize to prevent huge payload DoS (10 MB)
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
});

// Parse PostgreSQL URL from Railway/Neon to standard ADO.NET format
var connectionUrl = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrEmpty(connectionUrl) && connectionUrl.StartsWith("postgres"))
{
    var databaseUri = new Uri(connectionUrl);
    var userInfo = databaseUri.UserInfo.Split(':');
    var port = databaseUri.Port > 0 ? databaseUri.Port : 5432;
    var trustCert = builder.Environment.IsDevelopment() ? "Trust Server Certificate=true;" : ""; // M4: Strict SSL in Prod
    connectionUrl = $"Host={databaseUri.Host};Port={port};Database={databaseUri.LocalPath.TrimStart('/')};Username={userInfo[0]};Password={(userInfo.Length > 1 ? userInfo[1] : "")};SslMode=Require;{trustCert}";
}

// Only register Npgsql when a real connection string is available.
// Integration tests leave this empty and inject SQLite via ConfigureTestServices.
if (!string.IsNullOrEmpty(connectionUrl))
{
    builder.Services.AddDbContext<LocalListDbContext>(options =>
        options.UseNpgsql(connectionUrl));
}

// Add DI Services
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<AiProviderService>();

// Configure JSON formatting
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// Configure Authentication & Authorization — Firebase Auth
var firebaseProjectId = Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID")
    ?? builder.Configuration["Firebase:ProjectId"]
    ?? throw new InvalidOperationException("Firebase ProjectId is not configured. Set FIREBASE_PROJECT_ID env var.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = $"https://securetoken.google.com/{firebaseProjectId}";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"https://securetoken.google.com/{firebaseProjectId}",
            ValidateAudience = true,
            ValidAudience = firebaseProjectId,
            ValidateLifetime = true
        };
        // Disable Negotiate/Kerberos on the OIDC backchannel (not available in Alpine containers)
        options.BackchannelHttpHandler = new HttpClientHandler();
    });

builder.Services.AddAuthorization();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowSpecificOrigins", corsBuilder =>
    {
        var env = builder.Environment;
        var allowedOrigins = env.IsProduction() 
            ? new[] { "https://locallist.ai" }
            : new[] { "http://localhost:8081", "http://localhost:19006" };

        corsBuilder.WithOrigins(allowedOrigins)
            .WithMethods("GET", "POST", "PATCH", "DELETE", "OPTIONS")
            .WithHeaders("Content-Type", "Authorization")
            .AllowCredentials();
    });
});

// Configure Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? context.Request.Headers.Host.ToString(),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 100,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            }));
            
    // A3: Builder specific rate limits (5 per hour per IP) to prevent Gemini abuse
    options.AddPolicy("BuilderLimit", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 5,
                QueueLimit = 0,
                Window = TimeSpan.FromHours(1)
            }));

    // Auth brute-force protection: 10 requests per 15 minutes per IP
    options.AddPolicy("AuthLimit", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 10,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(15)
            }));

    // Admin endpoints: generous limit for internal tooling (bulk imports)
    options.AddPolicy("AdminLimit", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 60,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            }));

    options.RejectionStatusCode = 429;
});

// Add services to the container
builder.Services.AddOpenApi();

// M5 Fix: Register Antiforgery to prepare the backend for the Razor Admin ERP
builder.Services.AddAntiforgery();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        var exceptionHandlerPathFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();

        if (exceptionHandlerPathFeature?.Error is Exception exception)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogError(exception, "Unhandled exception");
        }

        // C2: Exception Handler restricts error details to Development environment
        if (app.Environment.IsDevelopment())
        {
            if (exceptionHandlerPathFeature?.Error is Exception ex)
            {
                await context.Response.WriteAsJsonAsync(new
                {
                    error = ex.Message,
                    inner = ex.InnerException?.Message,
                    type = ex.GetType().Name
                });
            }
        }
        else
        {
            await context.Response.WriteAsJsonAsync(new { error = "An internal server error occurred." });
        }
    });
});

// Resolve client IP behind Railway's reverse proxy (required for rate limiting)
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// A1: Inject Security Headers to mitigate XSS, Clickjacking, and framing
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
    context.Response.Headers.Append("Content-Security-Policy", "default-src 'none'");
    await next();
});

// Correlation ID middleware: read from request or generate new
app.Use(async (context, next) =>
{
    var requestId = context.Request.Headers["X-Request-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
    context.Response.Headers["X-Request-Id"] = requestId;
    using (Serilog.Context.LogContext.PushProperty("RequestId", requestId))
    {
        await next();
    }
});

app.UseSerilogRequestLogging();

app.UseHttpsRedirection();
app.UseCors("AllowSpecificOrigins");
app.UseRateLimiter();

// Setup Pipeline Security & Mapping
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/health", async (TimeProvider clock, LocalListDbContext db, CancellationToken ct) =>
{
    var dbOk = await db.Database.CanConnectAsync(ct);
    return new { status = dbOk ? "ok" : "degraded", version = "0.1.0", timestamp = clock.GetUtcNow() };
})
.WithName("HealthCheck")
;

app.Run();

// Make the implicit Program class accessible for WebApplicationFactory<Program> in tests
public partial class Program { }
