using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json.Serialization;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.I18n;
using LocalList.API.NET.Features.Auth.Services;
using LocalList.API.NET.Features.Builder;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Features.Chat.Services;
using LocalList.API.NET.Features.Admin.Places;
using LocalList.API.NET.Features.Routing;
using LocalList.API.NET.Features.Waitlist;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.IdentityModel.Tokens;
using System.IO.Compression;
using System.Threading.RateLimiting;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog(configuration => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Async(a => a.Console(), bufferSize: 10000, blockWhenFull: false));

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
    var isInternalNetwork = databaseUri.Host.EndsWith(".railway.internal");
    var sslMode = isInternalNetwork ? "Prefer" : "Require";
    var trustCert = (builder.Environment.IsDevelopment() || isInternalNetwork) ? "Trust Server Certificate=true;" : "";
    connectionUrl = $"Host={databaseUri.Host};Port={port};Database={databaseUri.LocalPath.TrimStart('/')};Username={userInfo[0]};Password={(userInfo.Length > 1 ? userInfo[1] : "")};SslMode={sslMode};{trustCert}Maximum Pool Size=50;Minimum Pool Size=5;Connection Idle Lifetime=60;";
}

// Only register Npgsql when a real connection string is available.
// Integration tests leave this empty and inject Postgres (Testcontainers) via ConfigureTestServices.
if (!string.IsNullOrEmpty(connectionUrl))
{
    // Bootstrap pgvector BEFORE building the pooled DataSource.
    // Npgsql caches pg_type on the first connection of each DataSource; if
    // `CREATE EXTENSION vector` runs later (inside an EF migration), the cache
    // stays stale and writes of Pgvector.Vector parameters fail with
    // "Cannot resolve 'vector' to a fully qualified datatype name."
    // Idempotent — extension already exists in prod; this covers fresh DBs.
    try
    {
        using var bootstrapConn = new NpgsqlConnection(connectionUrl);
        bootstrapConn.Open();
        using var bootstrapCmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector;", bootstrapConn);
        bootstrapCmd.ExecuteNonQuery();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[startup] pgvector bootstrap failed ({ex.GetType().Name}): {ex.Message}. Assuming extension is present and continuing.");
    }

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionUrl);
    dataSourceBuilder.UseVector();
    var dataSource = dataSourceBuilder.Build();

    builder.Services.AddDbContext<LocalListDbContext>(options =>
        options.UseNpgsql(dataSource, npg =>
        {
            npg.UseVector();
            // Cap explícito del tiempo que una query espera a DB. Sin esto, una DB colgada
            // puede bloquear request threads hasta el timeout default (30s). 10s es el
            // sweet-spot entre tolerancia a latencia transitoria y failure-fast.
            npg.CommandTimeout(10);
        }));
}

// Add DI Services
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<AiProviderService>(c => c.Timeout = TimeSpan.FromSeconds(25))
    .AddStandardResilienceHandler(options =>
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(25);
        options.CircuitBreaker.FailureRatio = 0.5;
        options.Retry.MaxRetryAttempts = 1;
        // Solo reintentar errores de red transitorios. Los 5xx son "duros" — el servicio
        // cae a keyword fallback inmediatamente (AiProviderService catch HttpRequestException).
        options.Retry.ShouldHandle = args => ValueTask.FromResult(
            args.Outcome.Exception is HttpRequestException);
    });

builder.Services.AddHttpClient<EmbeddingService>(c => c.Timeout = TimeSpan.FromSeconds(15))
    .AddStandardResilienceHandler(options =>
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);
        options.Retry.MaxRetryAttempts = 1;
        options.Retry.ShouldHandle = args => ValueTask.FromResult(
            args.Outcome.Exception is HttpRequestException);
    });

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<LanguageAccessor>();
builder.Services.AddScoped<PlaceRankingService>();
builder.Services.AddScoped<SchedulingService>();
builder.Services.AddScoped<PlanGenerationService>();
builder.Services.AddHttpClient<IRoutingService, MapboxRoutingService>(c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient<IGooglePlacesService, GooglePlacesService>(c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<RouteResolver>();
builder.Services.AddHttpClient<KlaviyoService>(c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddScoped<IEmailMarketingService, KlaviyoService>();

// Chat — slot-filling agent
builder.Services.AddHttpClient<SlotExtractorService>(c => c.Timeout = TimeSpan.FromSeconds(20))
    .AddStandardResilienceHandler(options =>
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
        options.Retry.MaxRetryAttempts = 1;
        options.Retry.ShouldHandle = args => ValueTask.FromResult(
            args.Outcome.Exception is HttpRequestException);
    });
builder.Services.AddScoped<ChatAgentService>();
builder.Services.AddScoped<ChatSecLogger>();

// Configure JSON formatting
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// Configure Authentication & Authorization — multi-scheme JWT.
// Two parallel schemes coexist: "Firebase" (RS256, used by /auth/sync + admin),
// "App" (HS256, used by the mobile app via /auth/signin|login|register|refresh).
// "Multi" is the policy scheme that picks one based on the token's `iss` claim.
var firebaseProjectId =
    Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID") is { Length: > 0 } envVar ? envVar
    : builder.Configuration["Firebase:ProjectId"] is { Length: > 0 } cfgVar ? cfgVar
    : throw new InvalidOperationException("Firebase ProjectId is not configured. Set FIREBASE_PROJECT_ID env var.");

const string FirebaseScheme = "Firebase";
const string AppScheme = "App";
const string MultiScheme = "Multi";

builder.Services.AddAuthentication(MultiScheme)
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
                        ?? builder.Configuration["Jwt:Secret"]
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

builder.Services.AddAuthorization();

// App auth services (HS256 JWT issuance, password hashing, OAuth ID token validation)
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();
builder.Services.AddSingleton<IAppleIdTokenValidator, AppleIdTokenValidator>();
builder.Services.AddSingleton<IGoogleIdTokenValidator, GoogleIdTokenValidator>();

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
            
    // A3: Builder specific rate limits per hour per IP to prevent Gemini abuse.
    // Configurable via env `Builder__RateLimitPerHour` (default 5). Pablo
    // 2026-04-26: durante testing intensivo override en Railway a 100+ para
    // no bloquearse; revertir antes de scale-out con usuarios reales.
    var builderLimit = builder.Configuration.GetValue<int?>("Builder:RateLimitPerHour") ?? 5;
    options.AddPolicy("BuilderLimit", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = builderLimit,
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

    // Waitlist: 5 requests per 60 seconds per IP (matches Landing edge rate limit)
    options.AddPolicy("WaitlistLimit", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 5,
                QueueLimit = 0,
                Window = TimeSpan.FromSeconds(60)
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

    // CitySearchLimit (anonymous): autocomplete on /cities/search. The endpoint
    // runs Unicode FormD normalization on every call → DoS vector if uncapped.
    // 30/min/IP is generous for real autocomplete (debounced at 250ms client-side
    // → max ~4 calls per typing burst).
    options.AddPolicy("CitySearchLimit", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 30,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            }));

    // CityCreateLimit (authenticated): partitioned by userId (NOT IP) so attackers
    // sharing an IP don't squeeze legit users. POST /cities writes to a public
    // anonymous-readable registry (XSS / SEO pollution surface), so cap is tight.
    options.AddPolicy("CityCreateLimit", context =>
    {
        var userId = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? context.User?.FindFirst("sub")?.Value
                     ?? context.Connection.RemoteIpAddress?.ToString()
                     ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: userId,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 10,
                QueueLimit = 0,
                Window = TimeSpan.FromHours(1)
            });
    });

    // ChatTurnLimit: sliding window 20/hr anonymous, 40/hr authenticated.
    // Sliding window prevents boundary exploitation vs fixed window.
    var chatLimitAnon = builder.Configuration.GetValue<int?>("Chat:RateLimitTurnsPerHourAnonymous") ?? 20;
    options.AddPolicy("ChatTurnLimit", context =>
    {
        var userId = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? context.User?.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(userId))
        {
            var authLimit = builder.Configuration.GetValue<int?>("Chat:RateLimitTurnsPerHourAuthenticated") ?? 40;
            return RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: $"chat_auth_{userId}",
                factory: _ => new SlidingWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = authLimit,
                    QueueLimit = 0,
                    Window = TimeSpan.FromHours(1),
                    SegmentsPerWindow = 6,
                });
        }
        return RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: $"chat_anon_{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = chatLimitAnon,
                QueueLimit = 0,
                Window = TimeSpan.FromHours(1),
                SegmentsPerWindow = 6,
            });
    });

    options.RejectionStatusCode = 429;
});

// Response compression (Brotli + Gzip). Registered before the pipeline uses it below.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

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
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    // Railway edge es el único proxy en prod. ForwardLimit=1 hace que solo se lea el último hop
    // (añadido por Railway). Cualquier XFF spoofeado por el cliente queda antes y se ignora.
    ForwardLimit = 1
};
forwardedOptions.KnownIPNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);

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

// Response compression must run upstream of HttpsRedirection and CORS so responses
// are compressed regardless of redirect/cors short-circuit paths.
app.UseResponseCompression();

app.UseHttpsRedirection();
app.UseCors("AllowSpecificOrigins");
app.UseRateLimiter();

// Setup Pipeline Security & Mapping
app.UseAuthentication();

// Push UserId into Serilog LogContext for every authenticated request.
// Must run after UseAuthentication (so context.User is populated) and before MapControllers.
app.Use(async (context, next) =>
{
    if (context.User?.Identity?.IsAuthenticated == true)
    {
        var userId = context.User.FindFirst("sub")?.Value
                     ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(userId))
        {
            using (Serilog.Context.LogContext.PushProperty("UserId", userId))
            {
                await next();
                return;
            }
        }
    }
    await next();
});

app.UseAuthorization();
app.MapControllers();

app.MapGet("/health", async (TimeProvider clock, LocalListDbContext db, CancellationToken ct) =>
{
    var dbOk = await db.Database.CanConnectAsync(ct);
    return new { status = dbOk ? "ok" : "degraded", version = "0.1.0", timestamp = clock.GetUtcNow() };
})
.WithName("HealthCheck")
;

// Apply pending EF Core migrations on startup (all migrations use idempotent raw SQL)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LocalListDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();

// Make the implicit Program class accessible for WebApplicationFactory<Program> in tests
public partial class Program { }
