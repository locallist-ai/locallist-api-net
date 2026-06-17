using System.Text.Json.Serialization;
using LocalList.API.NET.Shared.AI.Llm;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using System.IO.Compression;
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

builder.Services.AddPostgresDatabase(builder.Configuration, builder.Environment);
builder.Services.AddDomainServices(builder.Configuration);

// Configure JSON formatting
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddCorsPolicy(builder.Configuration, builder.Environment);
builder.Services.AddRateLimitingPolicies(builder.Configuration);

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

var geminiPresent = !string.IsNullOrEmpty(app.Configuration["Gemini:ApiKey"]);
var googlePlacesPresent = !string.IsNullOrEmpty(app.Configuration["GooglePlaces:ApiKey"]);
app.Logger.LogInformation("API keys at boot — Gemini:{Gemini} GooglePlaces:{Google}", geminiPresent, googlePlacesPresent);

var llmOptions = app.Configuration.GetSection(LlmOptions.SectionName).Get<LlmOptions>() ?? new LlmOptions();
LlmClientFactory.LogEffectiveChain(app.Configuration, llmOptions, app.Logger);

app.Run();

// Make the implicit Program class accessible for WebApplicationFactory<Program> in tests
public partial class Program { }
