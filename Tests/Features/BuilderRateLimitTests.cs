using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LocalList.API.Tests.Features;

/// <summary>
/// StartupFilter de test: repuebla <c>RemoteIpAddress</c> desde la cabecera
/// <c>X-Test-Client-Ip</c> ANTES del rate limiter, para que cada test pueda simular IPs
/// distintas y aislar los buckets particionados por IP (o compartir IP a propósito en el
/// test de account-farming). En producción esto lo hace ForwardedHeaders.
/// </summary>
public sealed class TestClientIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (ctx, n) =>
        {
            var hdr = ctx.Request.Headers["X-Test-Client-Ip"].FirstOrDefault();
            if (!string.IsNullOrEmpty(hdr) && System.Net.IPAddress.TryParse(hdr, out var ip))
                ctx.Connection.RemoteIpAddress = ip;
            await n();
        });
        next(app);
    };
}

/// <summary>
/// Base para fixtures que dejan ACTIVAS las políticas reales de rate-limiting (a diferencia
/// de <see cref="ApiFixture"/>, que las anula con GetNoLimiter) y registran el
/// <see cref="TestClientIpStartupFilter"/>. Las subclases fijan los tres límites de Builder.
/// </summary>
public abstract class RateLimitedFixtureBase : ApiFixture
{
    protected override bool DisableRateLimiting => false;

    protected abstract int AnonLimit { get; }
    protected abstract int AuthLimit { get; }
    protected abstract int IpCeiling { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Builder:RateLimitPerHour", AnonLimit.ToString());
        builder.UseSetting("Builder:RateLimitPerHourAuthenticated", AuthLimit.ToString());
        builder.UseSetting("Builder:RateLimitPerHourPerIp", IpCeiling.ToString());
        // Mismos números para /chat/turn (los tests comparten AnonLimit/AuthLimit/IpCeiling).
        builder.UseSetting("Chat:RateLimitTurnsPerHourAnonymous", AnonLimit.ToString());
        builder.UseSetting("Chat:RateLimitTurnsPerHourAuthenticated", AuthLimit.ToString());
        builder.UseSetting("Chat:RateLimitTurnsPerHourPerIp", IpCeiling.ToString());
        builder.ConfigureTestServices(s =>
            s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestClientIpStartupFilter>());
        base.ConfigureWebHost(builder);
    }
}

/// <summary>Techo por IP efectivamente desactivado (1000) para aislar el comportamiento por identidad.</summary>
public sealed class IdentityBucketFixture : RateLimitedFixtureBase
{
    protected override int AnonLimit => 2;
    protected override int AuthLimit => 5;
    protected override int IpCeiling => 1000;
}

/// <summary>Techo por IP bajo (3) para observar el corte anti-farming.</summary>
public sealed class IpCeilingFixture : RateLimitedFixtureBase
{
    protected override int AnonLimit => 2;
    protected override int AuthLimit => 5;
    protected override int IpCeiling => 3;
}

/// <summary>
/// Tests de integración del refinamiento por identidad de <c>POST /builder/chat</c> (techo
/// por IP desactivado). Verifican con el limiter REAL: límite anónimo por IP, bucket App
/// más alto, que un token Firebase NO obtiene el bucket alto, y aislamiento por IP.
/// </summary>
public class BuilderIdentityRateLimitTests : IClassFixture<IdentityBucketFixture>
{
    private readonly IdentityBucketFixture _fixture;
    public BuilderIdentityRateLimitTests(IdentityBucketFixture fixture) => _fixture = fixture;

    private HttpClient AnonClient(string ip)
    {
        var c = _fixture.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Client-Ip", ip);
        return c;
    }

    private static async Task<HttpResponseMessage> Gen(HttpClient c) =>
        await c.PostAsJsonAsync("/builder/chat", ValidBody());

    private static async Task<HttpResponseMessage> Turn(HttpClient c) =>
        await c.PostAsJsonAsync("/chat/turn", new { message = "hola" });

    private static object ValidBody() => new
    {
        message = "plan en Miami",
        tripContext = new { city = "Miami", days = 1, groupType = "couple" }
    };

    [Fact]
    public async Task Anonymous_ExceedsAnonLimit_Returns429()
    {
        var client = AnonClient("10.0.0.1");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Gen(client)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Gen(client)).StatusCode);
        // AnonLimit = 2 → el tercero se rechaza.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Gen(client)).StatusCode);
    }

    [Fact]
    public async Task AppAuthenticatedUser_HasHigherBucketThanAnon()
    {
        var uid = Guid.NewGuid();
        var client = await _fixture.CreateAppAuthenticatedClientWithUser(uid, $"rl-app-{uid}@test.com");
        client.DefaultRequestHeaders.Add("X-Test-Client-Ip", "10.0.0.2");

        // AuthLimit = 5 (> anon 2): los cinco primeros pasan.
        for (var i = 0; i < 5; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Gen(client)).StatusCode);

        // El sexto se rechaza.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Gen(client)).StatusCode);
    }

    [Fact]
    public async Task FirebaseToken_DoesNotGetHighBucket_CappedAtAnonLimit()
    {
        // Token Firebase (RS256) → ExtractAppUserId devuelve null → bucket anónimo por IP.
        var uid = Guid.NewGuid();
        var client = await _fixture.CreateAuthenticatedClientWithUser(uid, $"fb-{uid}", $"rl-fb-{uid}@test.com");
        client.DefaultRequestHeaders.Add("X-Test-Client-Ip", "10.0.0.3");

        // Capado al anon (2), NO al bucket alto (5): el tercero ya se rechaza.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Gen(client)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Gen(client)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Gen(client)).StatusCode);
    }

    [Fact]
    public async Task AnonBucket_IsPerIp_DoesNotBlockOtherIps()
    {
        // Agota el bucket anónimo de una IP.
        var flooder = AnonClient("10.0.0.4");
        await Gen(flooder);
        await Gen(flooder);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Gen(flooder)).StatusCode);

        // Otra IP anónima sigue funcionando (bucket independiente).
        var other = AnonClient("10.0.0.5");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Gen(other)).StatusCode);
    }

    [Fact]
    public async Task ChatTurn_FirebaseToken_DoesNotGetHighBucket_CappedAtAnonLimit()
    {
        // /chat/turn hereda el mismo tratamiento: token Firebase → ExtractAppUserId null →
        // bucket anónimo por IP (2), NO el bucket alto de chat-turn (5).
        var uid = Guid.NewGuid();
        var client = await _fixture.CreateAuthenticatedClientWithUser(uid, $"fb-ct-{uid}", $"rl-fbct-{uid}@test.com");
        client.DefaultRequestHeaders.Add("X-Test-Client-Ip", "10.0.0.6");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Turn(client)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Turn(client)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Turn(client)).StatusCode);
    }
}

/// <summary>
/// Test de integración del TECHO por IP anti account-farming (techo = 3). Verifica el
/// hallazgo crítico: registrar varias cuentas desde una misma IP NO permite superar el techo
/// por IP, porque el limiter encadenado del GlobalLimiter lo aplica antes que el bucket por
/// identidad. Contra el código previo (sin techo) N cuentas × su bucket superaban el límite.
/// </summary>
public class BuilderAccountFarmingTests : IClassFixture<IpCeilingFixture>
{
    private readonly IpCeilingFixture _fixture;
    public BuilderAccountFarmingTests(IpCeilingFixture fixture) => _fixture = fixture;

    private static object ValidBody() => new
    {
        message = "plan en Miami",
        tripContext = new { city = "Miami", days = 1, groupType = "couple" }
    };

    private async Task<List<HttpClient>> ThreeAppClientsSharingIp(string sharedIp, string tag)
    {
        var clients = new List<HttpClient>();
        for (var i = 0; i < 3; i++)
        {
            var uid = Guid.NewGuid();
            var c = await _fixture.CreateAppAuthenticatedClientWithUser(uid, $"{tag}-{uid}@test.com");
            c.DefaultRequestHeaders.Add("X-Test-Client-Ip", sharedIp);
            clients.Add(c);
        }
        return clients;
    }

    [Fact]
    public async Task ChatTurn_AccountFarming_MultipleAccountsSameIp_CannotExceedIpCeiling()
    {
        // Mismo ataque que en Builder, sobre /chat/turn: 3 cuentas App (bucket 5 c/u) desde
        // la misma IP. Techo por IP de chat-turn = 3 → 3 requests agotan el techo, la 4ª
        // (cuenta distinta, bucket propio intacto) → 429. Contra el código previo (sub crudo,
        // sin techo en chat-turn) cada cuenta llegaría a su bucket y el farming no tendría cota.
        var clients = await ThreeAppClientsSharingIp("10.9.9.10", "ctfarm");

        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await clients[0].PostAsJsonAsync("/chat/turn", new { message = "hola" })).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await clients[1].PostAsJsonAsync("/chat/turn", new { message = "hola" })).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await clients[2].PostAsJsonAsync("/chat/turn", new { message = "hola" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await clients[0].PostAsJsonAsync("/chat/turn", new { message = "hola" })).StatusCode);
    }

    [Fact]
    public async Task AccountFarming_MultipleAccountsSameIp_CannotExceedIpCeiling()
    {
        const string sharedIp = "10.9.9.9";

        // 3 cuentas App distintas (bucket individual = 5 c/u), TODAS desde la misma IP.
        var clients = new List<HttpClient>();
        for (var i = 0; i < 3; i++)
        {
            var uid = Guid.NewGuid();
            var c = await _fixture.CreateAppAuthenticatedClientWithUser(uid, $"farm-{uid}@test.com");
            c.DefaultRequestHeaders.Add("X-Test-Client-Ip", sharedIp);
            clients.Add(c);
        }

        // Techo por IP = 3: una request por cuenta agota el techo (ninguna cuenta agota su
        // bucket de 5), y la cuarta —desde cualquiera de las cuentas— se rechaza.
        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await clients[0].PostAsJsonAsync("/builder/chat", ValidBody())).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await clients[1].PostAsJsonAsync("/builder/chat", ValidBody())).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await clients[2].PostAsJsonAsync("/builder/chat", ValidBody())).StatusCode);

        // 4ª request total desde la misma IP (cuenta distinta, bucket propio intacto) → 429
        // por el techo por IP. Sin el techo, cada cuenta llegaría a 5 → farming sin cota.
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await clients[0].PostAsJsonAsync("/builder/chat", ValidBody())).StatusCode);
    }
}
