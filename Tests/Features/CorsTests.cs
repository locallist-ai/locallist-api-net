using System.Net;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Tests de integración de la política CORS (Program.cs, "AllowSpecificOrigins").
///
/// El admin web (Expo web) llama a la API desde un navegador, donde CORS sí aplica
/// (la app nativa nunca lo necesitó). Gap detectado al probar locallist-admin en web
/// por primera vez (jun 2026). Diseño en main (PR #96): los defaults dependen del
/// entorno y se AMPLÍAN sin redeploy vía Cors:AllowedOrigins (';'-separados); los
/// métodos incluyen PUT (lo usa la admin en PUT /admin/plans/{id}/stops).
///
/// El fixture corre en "Development", así que los defaults son
/// localhost:8081 / localhost:19006.
/// </summary>
public class CorsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Preflight_DefaultOrigin_ReturnsAllowOriginHeader()
    {
        var client = fixture.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/taxonomy");
        request.Headers.Add("Origin", "http://localhost:8081");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://localhost:8081",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("Authorization",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Headers")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preflight_PutMethod_IsAllowed()
    {
        // Regresión de PR #96: la admin web usa PUT /admin/plans/{id}/stops; sin PUT en
        // WithMethods el navegador bloquea el preflight. Este test fija ese contrato.
        var client = fixture.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/taxonomy");
        request.Headers.Add("Origin", "http://localhost:8081");
        request.Headers.Add("Access-Control-Request-Method", "PUT");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://localhost:8081",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("PUT",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Methods")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preflight_ConfiguredExtraOrigin_IsAllowed()
    {
        // Mecanismo de PR #96: Cors:AllowedOrigins amplía los defaults sin tocar código.
        // El preflight (OPTIONS) lo resuelve el middleware CORS sin tocar la DB, así que
        // la factory derivada no necesita migraciones.
        using var factory = fixture.WithWebHostBuilder(b =>
            b.UseSetting("Cors:AllowedOrigins", "http://localhost:8085;http://localhost:8084"));
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Options, "/taxonomy");
        request.Headers.Add("Origin", "http://localhost:8085");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://localhost:8085",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Preflight_UnknownOrigin_OmitsAllowOriginHeader()
    {
        var client = fixture.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/taxonomy");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Get_DefaultOrigin_AddsAllowOriginToActualResponse()
    {
        var client = fixture.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/taxonomy");
        request.Headers.Add("Origin", "http://localhost:8081");

        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal("http://localhost:8081",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }
}
