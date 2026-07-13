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

            // A3: /builder/chat y /chat/generate llaman a Gemini (coste real por request)
            // y son [AllowAnonymous] a propósito — v1 se lanza GRATIS y la app permite
            // generar plan sin cuenta (lib/api.ts solo adjunta Authorization si hay token).
            // Por eso NO forzamos login aquí (sería una decisión de producto que rompería la
            // UX gratuita); endurecemos el rate-limit y lo particionamos por identidad,
            // igual que ChatTurnLimit:
            //   - Autenticado (userId del JWT): bucket propio y límite más alto
            //     (`Builder:RateLimitPerHourAuthenticated`, default 20). Una cuenta real es
            //     más accountable y es el camino de retención; no debe competir con el ruido
            //     anónimo que comparte su IP.
            //   - Anónimo: bucket por IP y límite estricto anti-abuso de coste Gemini
            //     (`Builder:RateLimitPerHour`, default 5).
            // Residual conocido: usuarios anónimos tras un mismo NAT/CGNAT comparten el bucket
            // por IP. La identidad-por-dispositivo real (device attestation / App Check) es
            // trabajo aparte (project_app_hardening); NO metemos un header de dispositivo
            // spoofeable en la clave porque multiplicaría buckets por IP y DEBILITARÍA el
            // límite frente a un atacante que rota el header.
            var builderAnonLimit = configuration.GetValue<int?>("Builder:RateLimitPerHour")
                                   ?? DefaultBuilderAnonLimitPerHour;
            var builderAuthLimit = configuration.GetValue<int?>("Builder:RateLimitPerHourAuthenticated")
                                   ?? DefaultBuilderAuthLimitPerHour;
            options.AddPolicy("BuilderLimit", context =>
            {
                var (partitionKey, permitLimit) = ResolveBuilderPartition(
                    ExtractUserId(context),
                    context.Connection.RemoteIpAddress?.ToString(),
                    builderAnonLimit,
                    builderAuthLimit);
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: partitionKey,
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = permitLimit,
                        QueueLimit = 0,
                        Window = TimeSpan.FromHours(1)
                    });
            });

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

    // ── Builder/Chat-generate partitioning (identity-aware) ─────────────────────────
    // Expuesto internal (InternalsVisibleTo LocalList.API.Tests) para poder testear la
    // decisión de partición sin levantar un limiter real.

    /// <summary>Límite anónimo por hora y por IP para /builder/chat y /chat/generate.</summary>
    internal const int DefaultBuilderAnonLimitPerHour = 5;

    /// <summary>Límite autenticado por hora y por userId (bucket propio, más alto que el anónimo).</summary>
    internal const int DefaultBuilderAuthLimitPerHour = 20;

    /// <summary>
    /// Extrae el identificador estable de la identidad del JWT (userId de la app o uid de
    /// Firebase). Espejo de la lógica de ChatTurnLimit: JwtBearer mapea <c>sub</c> a
    /// <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>, pero comprobamos ambos
    /// por si el mapeo de claims inbound está desactivado.
    /// </summary>
    internal static string? ExtractUserId(HttpContext context) =>
        context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? context.User?.FindFirst("sub")?.Value;

    /// <summary>
    /// Decide la clave de partición y el límite de permisos para los endpoints caros de
    /// generación. Autenticado → bucket propio por identidad y límite alto; anónimo →
    /// bucket por IP y límite estricto. Nunca se mezclan las dos familias de claves.
    /// </summary>
    internal static (string partitionKey, int permitLimit) ResolveBuilderPartition(
        string? userId, string? ip, int anonLimit, int authLimit)
    {
        if (!string.IsNullOrEmpty(userId))
            return ($"builder_auth_{userId}", authLimit);

        return ($"builder_anon_{ip ?? "unknown"}", anonLimit);
    }
}
