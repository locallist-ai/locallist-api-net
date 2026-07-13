using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Fixture que deja ACTIVAS las políticas reales de rate-limiting (a diferencia de
/// <see cref="ApiFixture"/>, que las anula con GetNoLimiter) y baja los límites de la
/// política <c>BuilderLimit</c> a valores pequeños para poder observar el 429 en pocos
/// requests. Anon=2/h por IP · Auth=4/h por userId.
/// </summary>
public class RateLimitedApiFixture : ApiFixture
{
    protected override bool DisableRateLimiting => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Builder:RateLimitPerHour", "2");
        builder.UseSetting("Builder:RateLimitPerHourAuthenticated", "4");
        base.ConfigureWebHost(builder);
    }
}

/// <summary>
/// Tests de integración del rate-limiting identity-aware de <c>POST /builder/chat</c>.
///
/// El fix de hardening de v1 mantiene el endpoint [AllowAnonymous] (v1 es gratis y la app
/// permite generar plan sin cuenta) pero particiona el límite por identidad:
///   - Anónimo → bucket por IP, límite estricto (aquí 2/h) contra abuso de coste Gemini.
///   - Autenticado → bucket propio por userId, límite más alto (aquí 4/h), aislado del
///     ruido anónimo que comparte su IP.
///
/// Verificamos el comportamiento observable (rechazo/aceptación con 429) end-to-end con el
/// limiter REAL, no un mock. Sin sembrar places, la generación devuelve un plan efímero
/// vacío (200), suficiente para que el request consuma un permiso.
/// </summary>
public class BuilderRateLimitTests : IClassFixture<RateLimitedApiFixture>
{
    private readonly RateLimitedApiFixture _fixture;

    public BuilderRateLimitTests(RateLimitedApiFixture fixture) => _fixture = fixture;

    // 3 wizard signals (city + days + groupType) → pasa ValidateMinimumInput y llega a
    // generación; sin places sembrados el controller responde 200 con plan efímero.
    private static object ValidBody() => new
    {
        message = "plan en Miami",
        tripContext = new { city = "Miami", days = 1, groupType = "couple" }
    };

    [Fact]
    public async Task AnonymousFlood_ExceedsIpLimit_Returns429_ButAuthenticatedUserUnaffected()
    {
        var anon = _fixture.CreateClient();

        // Límite anónimo = 2/h por IP. Los dos primeros pasan.
        var first = await anon.PostAsJsonAsync("/builder/chat", ValidBody());
        var second = await anon.PostAsJsonAsync("/builder/chat", ValidBody());
        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, second.StatusCode);

        // El tercero, misma IP anónima, se rechaza.
        var third = await anon.PostAsJsonAsync("/builder/chat", ValidBody());
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);

        // Un usuario AUTENTICADO tiene su propio bucket por userId: no lo afecta el flood
        // anónimo que agotó el bucket por IP.
        var uid = "rl-unaffected-" + Guid.NewGuid();
        var authClient = await _fixture.CreateAuthenticatedClientWithUser(
            Guid.NewGuid(), uid, email: uid + "@test.com");
        var authResp = await authClient.PostAsJsonAsync("/builder/chat", ValidBody());
        Assert.NotEqual(HttpStatusCode.TooManyRequests, authResp.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUser_HasHigherOwnBucket_BeyondAnonLimit()
    {
        // userId único → bucket propio, aislado de cualquier otro test.
        var uid = "rl-higher-" + Guid.NewGuid();
        var client = await _fixture.CreateAuthenticatedClientWithUser(
            Guid.NewGuid(), uid, email: uid + "@test.com");

        // Límite autenticado = 4/h. Los cuatro primeros pasan (más que el anónimo de 2).
        for (var i = 0; i < 4; i++)
        {
            var resp = await client.PostAsJsonAsync("/builder/chat", ValidBody());
            Assert.NotEqual(HttpStatusCode.TooManyRequests, resp.StatusCode);
        }

        // El quinto, mismo usuario, se rechaza.
        var fifth = await client.PostAsJsonAsync("/builder/chat", ValidBody());
        Assert.Equal(HttpStatusCode.TooManyRequests, fifth.StatusCode);
    }
}
