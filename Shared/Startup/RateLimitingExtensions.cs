using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using LocalList.API.NET.Features.Auth.Services;

namespace LocalList.API.NET.Shared.Startup;

public static class RateLimitingExtensions
{
    /// <summary>
    /// Registra el GlobalLimiter (encadenado: burst 100/min/IP + techo horario por IP para
    /// los endpoints caros de generación) y las políticas por endpoint (Builder, Auth,
    /// Waitlist, Admin, CitySearch, CityCreate, ChatTurn).
    ///
    /// IMPORTANTE (orden de pipeline): <c>app.UseRateLimiter()</c> DEBE ir después de
    /// <c>app.UseAuthentication()</c>, si no <c>context.User</c> está vacío y las políticas
    /// identity-aware caen todas al bucket anónimo por IP.
    /// </summary>
    public static IServiceCollection AddRateLimitingPolicies(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Techo horario por IP para /builder/chat y /chat/generate. Es el ancla anti-abuso:
        // acota el "account farming" (registrar N cuentas desde 1 IP para multiplicar la
        // cuota autenticada) porque TODO el tráfico de esos endpoints desde una IP —anónimo
        // y de cualquier número de cuentas— comparte este techo. Ver comentario extenso en
        // la política BuilderLimit.
        var builderIpCeiling = configuration.GetValue<int?>("Builder:RateLimitPerHourPerIp")
                               ?? DefaultBuilderIpCeilingPerHour;

        services.AddRateLimiter(options =>
        {
            // ── GlobalLimiter encadenado ────────────────────────────────────────────────
            // CreateChained aplica AMBOS limiters a cada request; gana el más restrictivo.
            //   (1) burst 100/min/IP en todos los endpoints (anti-ráfaga general).
            //   (2) techo horario por IP SOLO en los endpoints de generación (BuilderLimit);
            //       para el resto devuelve NoLimiter.
            var burstLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString()
                                  ?? context.Request.Headers.Host.ToString(),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = 100,
                        QueueLimit = 0,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            var builderIpCeilingLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!IsExpensiveGenEndpoint(context))
                    return RateLimitPartition.GetNoLimiter<string>("non_builder");

                var ip = ResolveIpOrWarn(context, "builder_ip_ceiling");
                return RateLimitPartition.Get(
                    $"builder_ip_ceiling_{ip}",
                    _ => CreateBuilderIpCeilingLimiter(builderIpCeiling));
            });

            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                burstLimiter, builderIpCeilingLimiter);

            // ── BuilderLimit: refinamiento por identidad (bajo el techo por IP) ──────────
            // /builder/chat y /chat/generate llaman a Gemini (coste real por request) y son
            // [AllowAnonymous] a propósito — v1 se lanza GRATIS y la app permite generar plan
            // sin cuenta (lib/api.ts solo adjunta Authorization si hay token). NO forzamos
            // login (sería decisión de producto que rompería la UX gratuita). En su lugar,
            // cada request pasa por DOS capas (el más restrictivo gana):
            //   (a) TECHO por IP (GlobalLimiter encadenado, `Builder:RateLimitPerHourPerIp`,
            //       default 60/h): un atacante que registra N cuentas desde 1 IP NO puede
            //       superar este techo — autenticarse ya no ESCALA la cuota, solo refina.
            //   (b) Bucket por IDENTIDAD (esta política, sliding window):
            //         - App token (AppScheme HS256, ExtractAppUserId): bucket propio por
            //           userId, límite más alto (`Builder:RateLimitPerHourAuthenticated`,
            //           default 20). Es el usuario real de la app y el camino de retención.
            //         - Cualquier otra cosa (anónimo, token Firebase, malformado): bucket por
            //           IP, límite estricto (`Builder:RateLimitPerHour`, default 5).
            // Por qué SOLO AppScheme obtiene el bucket alto (no cualquier token con `sub`):
            // los endpoints aceptan tokens Firebase, y si se habilitara Firebase Anonymous
            // Auth serían UIDs ilimitados que se saltarían el throttle de /auth/register. Los
            // tokens Firebase quedan por tanto en el bucket anónimo por IP.
            // Por qué NO un device header en la clave: un header spoofeable multiplicaría
            // buckets por IP y DEBILITARÍA el límite ante un atacante que lo rota; la
            // identidad-por-dispositivo real (App Check) es trabajo aparte
            // (project_app_hardening).
            // Residual: usuarios legítimos tras un mismo CGNAT comparten el techo por IP.
            var builderAnonLimit = configuration.GetValue<int?>("Builder:RateLimitPerHour")
                                   ?? DefaultBuilderAnonLimitPerHour;
            var builderAuthLimit = configuration.GetValue<int?>("Builder:RateLimitPerHourAuthenticated")
                                   ?? DefaultBuilderAuthLimitPerHour;
            options.AddPolicy("BuilderLimit", context =>
            {
                var (partitionKey, permitLimit) = ResolveBuilderPartition(
                    ExtractAppUserId(context),
                    ResolveIpOrWarn(context, "builder_identity"),
                    builderAnonLimit,
                    builderAuthLimit);
                return RateLimitPartition.Get(
                    partitionKey,
                    _ => CreateBuilderLimiter(permitLimit));
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
    // decisión de partición y el tipo de limiter sin levantar el pipeline completo.

    /// <summary>Límite anónimo por hora y por IP para /builder/chat y /chat/generate.</summary>
    internal const int DefaultBuilderAnonLimitPerHour = 5;

    /// <summary>Límite por hora del bucket App-autenticado (por userId, refinamiento bajo el techo por IP).</summary>
    internal const int DefaultBuilderAuthLimitPerHour = 20;

    /// <summary>
    /// Techo horario por IP para los endpoints de generación. Es el ancla anti-farming:
    /// ninguna IP puede superarlo por más cuentas que registre. Elegido generoso para no
    /// romper NAT pequeños legítimos (casa/oficina: 60/h = 3× el bucket auth) pero acotando
    /// duro el abuso (antes: N cuentas × 20/h = ilimitado). Tunable vía
    /// <c>Builder:RateLimitPerHourPerIp</c>; bajar si aparece abuso desde CGNAT.
    /// </summary>
    internal const int DefaultBuilderIpCeilingPerHour = 60;

    /// <summary>
    /// Devuelve el userId SOLO si el token es de la app (AppScheme HS256, issuer
    /// <see cref="JwtTokenService.Issuer"/>). Los tokens Firebase — incluido Firebase
    /// Anonymous Auth, que emitiría UIDs ilimitados — NO obtienen el bucket alto: caen al
    /// bucket anónimo por IP para no saltarse el throttle de /auth/register.
    /// JwtBearer mapea <c>sub</c> a <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>;
    /// comprobamos ambos por si el mapeo inbound está desactivado. El <c>Issuer</c> de la
    /// propia claim refleja el <c>iss</c> validado del token.
    /// </summary>
    internal static string? ExtractAppUserId(HttpContext context)
    {
        var idClaim = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)
                      ?? context.User?.FindFirst("sub");
        if (idClaim is null || string.IsNullOrEmpty(idClaim.Value))
            return null;

        return idClaim.Issuer == JwtTokenService.Issuer ? idClaim.Value : null;
    }

    /// <summary>
    /// Decide la clave de partición y el límite para el refinamiento por identidad de los
    /// endpoints caros. App-autenticado → bucket propio por userId y límite alto; resto →
    /// bucket por IP y límite estricto. Las dos familias de claves nunca colisionan.
    /// </summary>
    internal static (string partitionKey, int permitLimit) ResolveBuilderPartition(
        string? appUserId, string? ip, int anonLimit, int authLimit)
    {
        if (!string.IsNullOrEmpty(appUserId))
            return ($"builder_auth_{appUserId}", authLimit);

        return ($"builder_anon_{ip ?? "unknown"}", anonLimit);
    }

    /// <summary>
    /// Limiter del refinamiento por identidad: sliding window (evita el boundary-doubling de
    /// la ventana fija — hasta 2× el límite a caballo del cambio de hora — en el endpoint más
    /// caro), consistente con ChatTurnLimit.
    /// </summary>
    internal static RateLimiter CreateBuilderLimiter(int permitLimit) =>
        new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = permitLimit,
            QueueLimit = 0,
            Window = TimeSpan.FromHours(1),
            SegmentsPerWindow = 6,
        });

    /// <summary>Limiter del techo por IP: sliding window por consistencia anti-boundary.</summary>
    internal static RateLimiter CreateBuilderIpCeilingLimiter(int permitLimit) =>
        new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = permitLimit,
            QueueLimit = 0,
            Window = TimeSpan.FromHours(1),
            SegmentsPerWindow = 6,
        });

    /// <summary>
    /// True si el endpoint actual usa la política BuilderLimit (/builder/chat y
    /// /chat/generate). Se ata a la metadata de la política, no a rutas hardcodeadas, así
    /// que el techo por IP cubre automáticamente cualquier endpoint que la adopte.
    /// </summary>
    private static bool IsExpensiveGenEndpoint(HttpContext context)
    {
        var meta = context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>();
        return meta?.PolicyName == "BuilderLimit";
    }

    private static int _ipUnresolvedLogged;

    /// <summary>
    /// Devuelve la IP remota o "unknown". Riesgo operacional (auto-DoS): todo el bucket
    /// anónimo/techo por IP depende de que ForwardedHeaders repueble RemoteIpAddress detrás
    /// del proxy (Railway). Si falla, TODO el tráfico anónimo colapsa en una sola partición
    /// "unknown" y comparte 5/h. Logueamos una vez a modo de alarma temprana. NOTA PABLO:
    /// smoke-test en prod antes del pico de TikTok — hacer una request anónima y confirmar
    /// en logs que la partición NO es "unknown".
    /// </summary>
    private static string ResolveIpOrWarn(HttpContext context, string usage)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrEmpty(ip))
            return ip;

        if (Interlocked.CompareExchange(ref _ipUnresolvedLogged, 1, 0) == 0)
        {
            var logger = context.RequestServices
                .GetService(typeof(ILoggerFactory)) as ILoggerFactory;
            logger?.CreateLogger("LocalList.RateLimiting").LogWarning(
                "RATE-LIMIT: RemoteIpAddress no resuelto en {Usage}; el bucket anónimo/techo por " +
                "IP colapsa a 'unknown' (posible auto-DoS). Verifica ForwardedHeaders/proxy en prod.",
                usage);
        }
        return "unknown";
    }
}
