using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using LocalList.API.NET.Features.Auth.Services;

namespace LocalList.API.NET.Shared.Startup;

public static class RateLimitingExtensions
{
    /// <summary>
    /// Registra el GlobalLimiter (encadenado: burst 100/min/IP + techo horario por IP para
    /// los endpoints "medidos" que queman Gemini —BuilderLimit y ChatTurnLimit—) y las
    /// políticas por endpoint (Builder, Auth, Waitlist, Admin, CitySearch, CityCreate,
    /// ChatTurn).
    ///
    /// IMPORTANTE (orden de pipeline): <c>app.UseRateLimiter()</c> DEBE ir después de
    /// <c>app.UseAuthentication()</c>, si no <c>context.User</c> está vacío y las políticas
    /// identity-aware caen todas al bucket anónimo por IP.
    /// </summary>
    public static IServiceCollection AddRateLimitingPolicies(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Techos horarios por IP de los endpoints medidos. Anclan el anti-farming: ninguna
        // IP puede superarlos por más cuentas que registre. Cada endpoint tiene su propio
        // namespace y número (los turnos de chat son más frecuentes y baratos que una
        // generación completa). Ver comentario extenso en la política BuilderLimit.
        var builderIpCeiling = configuration.GetValue<int?>("Builder:RateLimitPerHourPerIp")
                               ?? DefaultBuilderIpCeilingPerHour;
        var chatTurnIpCeiling = configuration.GetValue<int?>("Chat:RateLimitTurnsPerHourPerIp")
                                ?? DefaultChatTurnIpCeilingPerHour;

        services.AddRateLimiter(options =>
        {
            // ── GlobalLimiter encadenado ────────────────────────────────────────────────
            // CreateChained aplica AMBOS limiters a cada request; gana el más restrictivo.
            //   (1) burst 100/min/IP en todos los endpoints (anti-ráfaga general).
            //   (2) techo horario por IP SOLO en los endpoints medidos (BuilderLimit /
            //       ChatTurnLimit); para el resto devuelve NoLimiter.
            var burstLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    // Fallback a "unknown" (no a Host, que es spoofeable): si ForwardedHeaders
                    // fallara, no queremos que un atacante obtenga buckets de burst frescos
                    // rotando la cabecera Host.
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = 100,
                        QueueLimit = 0,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            var meteredIpCeilingLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var (prefix, ceiling) = ResolveMeteredIpCeiling(context, builderIpCeiling, chatTurnIpCeiling);
                if (prefix is null)
                    return RateLimitPartition.GetNoLimiter<string>("non_metered");

                var ip = ResolveIpOrWarn(context, prefix);
                return RateLimitPartition.Get(
                    $"{prefix}_{ip}",
                    _ => CreateSlidingHourlyLimiter(ceiling));
            });

            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                burstLimiter, meteredIpCeilingLimiter);

            // ── BuilderLimit: refinamiento por identidad (bajo el techo por IP) ──────────
            // /builder/chat y /chat/generate llaman a Gemini (coste real por request). Desde
            // F4 (catálogo Plus) ambos exigen [Authorize] — sin identidad no hay contador
            // mensual posible — así que el tráfico ANÓNIMO a estos endpoints ya nunca llega a
            // Gemini: muere en 401 en la autorización. El bucket anónimo de esta política NO
            // está muerto: el rate limiter corre ANTES de la autorización (después de
            // UseAuthentication), de modo que sigue acotando el spam de requests sin token
            // (barato, pero spam) y protege el pipeline pre-401. El funnel anónimo real vive
            // en /chat/turn (ChatTurnLimit). Cada request pasa por DOS capas (el más
            // restrictivo gana):
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
                    _ => CreateSlidingHourlyLimiter(permitLimit));
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

            // RevenueCatWebhookLimit (anonymous, F4): the billing webhook triggers an outbound
            // RevenueCat REST lookup per fresh event. An actor with the shared secret could flood
            // fresh rc_event_ids to make RevenueCat 429 us → legit lookups degrade to 503 and real
            // upgrades stall (a DoS on revenue). Cap per IP, tighter than the global 100/min, on
            // top of Kestrel's 10 MB body cap. RevenueCat delivers from a small set of IPs and
            // retries with backoff, so 60/min/IP is ample for genuine traffic.
            options.AddPolicy("RevenueCatWebhookLimit", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = 60,
                        QueueLimit = 0,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            // ── ChatTurnLimit: mismo tratamiento que BuilderLimit ────────────────────────
            // /chat/turn también llama a Gemini por turno y es [AllowAnonymous]. Pasa por el
            // techo por IP encadenado (`Chat:RateLimitTurnsPerHourPerIp`, default 120/h,
            // namespace propio) Y el bucket por identidad (sliding window):
            //   - App token (ExtractAppUserId, solo AppScheme): bucket propio por userId
            //     (`Chat:RateLimitTurnsPerHourAuthenticated`, default 40).
            //   - Anónimo / Firebase / otros: bucket por IP
            //     (`Chat:RateLimitTurnsPerHourAnonymous`, default 20).
            // El check de issuer cierra el mismo bypass que en BuilderLimit: un UID de
            // Firebase Anonymous Auth ya no obtiene el bucket alto (40) — cae al bucket por IP.
            var chatLimitAnon = configuration.GetValue<int?>("Chat:RateLimitTurnsPerHourAnonymous")
                                ?? DefaultChatTurnAnonLimitPerHour;
            var chatLimitAuth = configuration.GetValue<int?>("Chat:RateLimitTurnsPerHourAuthenticated")
                                ?? DefaultChatTurnAuthLimitPerHour;
            options.AddPolicy("ChatTurnLimit", context =>
            {
                var (partitionKey, permitLimit) = ResolveChatTurnPartition(
                    ExtractAppUserId(context),
                    ResolveIpOrWarn(context, "chatturn_identity"),
                    chatLimitAnon,
                    chatLimitAuth);
                return RateLimitPartition.Get(
                    partitionKey,
                    _ => CreateSlidingHourlyLimiter(permitLimit));
            });

            options.RejectionStatusCode = 429;
        });

        return services;
    }

    // ── Partición identity-aware de los endpoints medidos (Builder/Chat) ────────────
    // Expuesto internal (InternalsVisibleTo LocalList.API.Tests) para poder testear la
    // decisión de partición y el tipo de limiter sin levantar el pipeline completo.

    /// <summary>Límite anónimo por hora y por IP para /builder/chat y /chat/generate.</summary>
    internal const int DefaultBuilderAnonLimitPerHour = 5;

    /// <summary>Límite por hora del bucket App-autenticado de Builder (por userId, bajo el techo por IP).</summary>
    internal const int DefaultBuilderAuthLimitPerHour = 20;

    /// <summary>
    /// Techo horario por IP de Builder. Anti-farming: ninguna IP lo supera por más cuentas
    /// que registre. Generoso para no romper NAT pequeños (60/h = 3× el bucket auth) pero
    /// acota duro el abuso (antes: N cuentas × 20/h = ilimitado). Tunable vía
    /// <c>Builder:RateLimitPerHourPerIp</c>.
    /// </summary>
    internal const int DefaultBuilderIpCeilingPerHour = 60;

    /// <summary>Límite anónimo por hora y por IP de /chat/turn.</summary>
    internal const int DefaultChatTurnAnonLimitPerHour = 20;

    /// <summary>Límite por hora del bucket App-autenticado de /chat/turn (por userId, bajo el techo por IP).</summary>
    internal const int DefaultChatTurnAuthLimitPerHour = 40;

    /// <summary>
    /// Techo horario por IP de /chat/turn. Mismo ratio que Builder (3× el bucket auth = 120/h)
    /// pero mayor en absoluto porque los turnos son más frecuentes que una generación
    /// completa. Tunable vía <c>Chat:RateLimitTurnsPerHourPerIp</c>.
    /// </summary>
    internal const int DefaultChatTurnIpCeilingPerHour = 120;

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

    /// <summary>Partición identity-aware de BuilderLimit (prefijos builder_auth_/builder_anon_).</summary>
    internal static (string partitionKey, int permitLimit) ResolveBuilderPartition(
        string? appUserId, string? ip, int anonLimit, int authLimit)
        => ResolveIdentityPartition("builder_auth_", "builder_anon_", appUserId, ip, anonLimit, authLimit);

    /// <summary>Partición identity-aware de ChatTurnLimit (prefijos chat_auth_/chat_anon_).</summary>
    internal static (string partitionKey, int permitLimit) ResolveChatTurnPartition(
        string? appUserId, string? ip, int anonLimit, int authLimit)
        => ResolveIdentityPartition("chat_auth_", "chat_anon_", appUserId, ip, anonLimit, authLimit);

    /// <summary>
    /// Núcleo compartido: App-autenticado → bucket propio por userId y límite alto; resto →
    /// bucket por IP y límite estricto. Las dos familias de claves nunca colisionan.
    /// </summary>
    private static (string partitionKey, int permitLimit) ResolveIdentityPartition(
        string authPrefix, string anonPrefix, string? appUserId, string? ip, int anonLimit, int authLimit)
    {
        if (!string.IsNullOrEmpty(appUserId))
            return ($"{authPrefix}{appUserId}", authLimit);

        return ($"{anonPrefix}{ip ?? "unknown"}", anonLimit);
    }

    /// <summary>
    /// Limiter sliding window horario (6 segmentos). Sliding evita el boundary-doubling de la
    /// ventana fija — hasta 2× el límite a caballo del cambio de hora — en los endpoints más
    /// caros. Usado por el refinamiento por identidad y por el techo por IP.
    /// </summary>
    internal static RateLimiter CreateSlidingHourlyLimiter(int permitLimit) =>
        new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = permitLimit,
            QueueLimit = 0,
            Window = TimeSpan.FromHours(1),
            SegmentsPerWindow = 6,
        });

    /// <summary>
    /// Si el endpoint actual es "medido" (usa la política BuilderLimit o ChatTurnLimit),
    /// devuelve su prefijo de partición del techo y su valor; si no, (null, 0). Se ata a la
    /// metadata de la política, no a rutas hardcodeadas.
    /// </summary>
    internal static (string? prefix, int ceiling) ResolveMeteredIpCeiling(
        HttpContext context, int builderCeiling, int chatTurnCeiling)
    {
        var policy = context.GetEndpoint()?.Metadata
            .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        return policy switch
        {
            "BuilderLimit" => ("builder_ip_ceiling", builderCeiling),
            "ChatTurnLimit" => ("chatturn_ip_ceiling", chatTurnCeiling),
            _ => (null, 0),
        };
    }

    private static int _ipUnresolvedLogged;

    /// <summary>
    /// Devuelve la IP remota o "unknown". Riesgo operacional (auto-DoS): todo el bucket
    /// anónimo/techo por IP depende de que ForwardedHeaders repueble RemoteIpAddress detrás
    /// del proxy (Railway). Si falla, TODO el tráfico anónimo colapsa en una sola partición
    /// "unknown". Logueamos una vez a modo de alarma temprana. NOTA PABLO: smoke-test en prod
    /// antes del pico de TikTok — hacer una request anónima y confirmar en logs que la
    /// partición NO es "unknown".
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
