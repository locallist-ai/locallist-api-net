using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Tests del endpoint POST /cities/request — feedback "¿No ves tu ciudad?"
/// (Pablo 2026-07-25). Sobre ApiFixture (Testcontainers PostgreSQL real).
/// </summary>
public class CityRequestsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture = fixture;

    private record ErrorResponse(string Error);
    private record MessageResponse(string Message);

    // (a) POST válido anónimo → 201 + fila con normalized_city correcto (diacríticos agrupados).
    [Fact]
    public async Task Post_ValidAnonymous_Returns201AndStoresNormalized()
    {
        var client = _fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/cities/request", new { city = "Málaga" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MessageResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body!.Message));

        var db = _fixture.GetDbContext();
        var row = await db.CityRequests.FirstOrDefaultAsync(cr => cr.CityText == "Málaga");
        Assert.NotNull(row);
        Assert.Equal("malaga", row!.NormalizedCity);
        Assert.Null(row.UserId); // anónimo
    }

    // (b) Autenticado → user_id poblado.
    [Fact]
    public async Task Post_Authenticated_PopulatesUserId()
    {
        var userId = Guid.NewGuid();
        var client = await _fixture.CreateAppAuthenticatedClientWithUser(userId, $"ur-{userId:N}@example.com");

        var response = await client.PostAsJsonAsync("/cities/request", new { city = "Cityauthtoken" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var db = _fixture.GetDbContext();
        var row = await db.CityRequests.FirstOrDefaultAsync(cr => cr.NormalizedCity == "cityauthtoken");
        Assert.NotNull(row);
        Assert.Equal(userId, row!.UserId);
    }

    // (c) >100 chars → 400 city_too_long, sin fila.
    [Fact]
    public async Task Post_TooLong_Returns400AndNoRow()
    {
        var tooLong = new string('a', 101);
        var client = _fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/cities/request", new { city = tooLong });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("city_too_long", body!.Error);

        var db = _fixture.GetDbContext();
        Assert.False(await db.CityRequests.AnyAsync(cr => cr.CityText == tooLong));
    }

    // (d) <script> / javascript: → 400 city_invalid, sin fila.
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://evil.example.com")]
    [InlineData("🚀🚀🚀")]
    public async Task Post_MaliciousOrJunk_Returns400Invalid(string payload)
    {
        var client = _fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/cities/request", new { city = payload });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("city_invalid", body!.Error);

        var db = _fixture.GetDbContext();
        Assert.False(await db.CityRequests.AnyAsync(cr => cr.CityText == payload));
    }

    // Whitespace interno NO horizontal (\n/\t/\r) → 400 city_invalid (la regex usa
    // \p{Zs}, no \s): conducta elegida = RECHAZO, no colapso. city_text nunca
    // guarda filas multilínea.
    [Theory]
    [InlineData("Sevilla\nDROP TABLE")]
    [InlineData("Sevilla\tfoo")]
    [InlineData("Sevi\rlla")]
    public async Task Post_InternalControlWhitespace_Returns400InvalidAndNoRow(string payload)
    {
        var client = _fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/cities/request", new { city = payload });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("city_invalid", body!.Error);

        // Ni la fila cruda ni ninguna fila con \n/\t/\r se ha guardado.
        var db = _fixture.GetDbContext();
        Assert.False(await db.CityRequests.AnyAsync(cr => cr.CityText == payload));
        var texts = await db.CityRequests.Select(cr => cr.CityText).ToListAsync();
        Assert.DoesNotContain(texts, t => t.Contains('\n') || t.Contains('\t') || t.Contains('\r'));
    }

    // El espacio horizontal normal sigue siendo válido (regresión del fix \s→\p{Zs}).
    [Fact]
    public async Task Post_NameWithRegularSpaces_StillReturns201()
    {
        var client = _fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/cities/request", new { city = "San Sebastián de los Reyes" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var db = _fixture.GetDbContext();
        Assert.True(await db.CityRequests.AnyAsync(cr => cr.NormalizedCity == "san sebastian de los reyes"));
    }

    // (e) Vacío → 400 city_required.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Post_Empty_Returns400Required(string payload)
    {
        var client = _fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/cities/request", new { city = payload });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("city_required", body!.Error);
    }
}

/// <summary>
/// Fixture IP-aware con rate limiting DESACTIVADO (default): registra el
/// <see cref="TestClientIpStartupFilter"/> para poder fijar RemoteIpAddress vía
/// cabecera y así ejercitar el dedup por ip_hash (que se salta cuando la IP no
/// resuelve). Reutiliza el filtro definido en BuilderRateLimitTests.
/// </summary>
public sealed class CityRequestIpFixture : ApiFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(s =>
            s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestClientIpStartupFilter>());
        base.ConfigureWebHost(builder);
    }
}

public class CityRequestDedupTests(CityRequestIpFixture fixture) : IClassFixture<CityRequestIpFixture>
{
    private readonly CityRequestIpFixture _fixture = fixture;

    private HttpClient ClientFromIp(string ip)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Client-Ip", ip);
        return client;
    }

    // (f) Dedup 24h: misma IP + misma ciudad → 200 sin fila nueva; ciudad distinta → inserta.
    [Fact]
    public async Task Post_SameIpSameCity_DedupsWithin24h()
    {
        var client = ClientFromIp("10.20.30.40");

        var first = await client.PostAsJsonAsync("/cities/request", new { city = "Dedupville" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/cities/request", new { city = "Dedupville" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // idempotente, no 201

        var db = _fixture.GetDbContext();
        var count = await db.CityRequests.CountAsync(cr => cr.NormalizedCity == "dedupville");
        Assert.Equal(1, count);

        // Ciudad distinta desde la MISMA IP → sí inserta.
        var other = await client.PostAsJsonAsync("/cities/request", new { city = "Otherdedupville" });
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.True(await db.CityRequests.AnyAsync(cr => cr.NormalizedCity == "otherdedupville"));
    }

    // Dedup es POR IP: otra IP pidiendo la misma ciudad sí inserta una fila propia.
    [Fact]
    public async Task Post_DifferentIpSameCity_Inserts()
    {
        var clientA = ClientFromIp("10.0.0.1");
        var clientB = ClientFromIp("10.0.0.2");

        Assert.Equal(HttpStatusCode.Created,
            (await clientA.PostAsJsonAsync("/cities/request", new { city = "Sharedcity" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await clientB.PostAsJsonAsync("/cities/request", new { city = "Sharedcity" })).StatusCode);

        var db = _fixture.GetDbContext();
        Assert.Equal(2, await db.CityRequests.CountAsync(cr => cr.NormalizedCity == "sharedcity"));
    }
}

/// <summary>
/// Fixture con rate limiting REAL activo + IP-aware, para verificar la política
/// CityRequestLimit (5/60s por IP).
/// </summary>
public sealed class CityRequestRateLimitFixture : ApiFixture
{
    protected override bool DisableRateLimiting => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(s =>
            s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestClientIpStartupFilter>());
        base.ConfigureWebHost(builder);
    }
}

public class CityRequestRateLimitTests(CityRequestRateLimitFixture fixture) : IClassFixture<CityRequestRateLimitFixture>
{
    private readonly CityRequestRateLimitFixture _fixture = fixture;

    // (g) Rate limit: la 6ª petición dentro de la ventana desde la misma IP → 429.
    [Fact]
    public async Task Post_ExceedsLimit_Returns429()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Client-Ip", "192.0.2.55");

        // 5 ciudades distintas (evita que el dedup responda 200) → todas 201.
        // Nombres solo-letras (la regex de dominio rechaza dígitos).
        var names = new[] { "Ratelimitcitya", "Ratelimitcityb", "Ratelimitcityc", "Ratelimitcityd", "Ratelimitcitye" };
        foreach (var name in names)
        {
            var ok = await client.PostAsJsonAsync("/cities/request", new { city = name });
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        var blocked = await client.PostAsJsonAsync("/cities/request", new { city = "Ratelimitcityx" });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }
}
