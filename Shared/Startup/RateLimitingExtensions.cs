using System.Threading.RateLimiting;

namespace LocalList.API.NET.Shared.Startup;

public static class RateLimitingExtensions
{
    /// <summary>
    /// Registers the global 100/min/IP limiter plus per-endpoint policies
    /// (Builder, Auth, Waitlist, Admin, CitySearch, CityCreate, ChatTurn).
    /// </summary>
    public static IServiceCollection AddRateLimitingPolicies(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddRateLimiter(options =>
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
            var builderLimit = configuration.GetValue<int?>("Builder:RateLimitPerHour") ?? 5;
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
            var chatLimitAnon = configuration.GetValue<int?>("Chat:RateLimitTurnsPerHourAnonymous") ?? 20;
            options.AddPolicy("ChatTurnLimit", context =>
            {
                var userId = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                             ?? context.User?.FindFirst("sub")?.Value;
                if (!string.IsNullOrEmpty(userId))
                {
                    var authLimit = configuration.GetValue<int?>("Chat:RateLimitTurnsPerHourAuthenticated") ?? 40;
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

        return services;
    }
}
