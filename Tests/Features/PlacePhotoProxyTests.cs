using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

// ── Fixtures ────────────────────────────────────────────────────────────────────────

/// <summary>
/// Fixture con la key SEPARADA (PhotoApiKey) configurada, y ADEMÁS una ApiKey de ingesta
/// DISTINTA. Sirve para el camino feliz y para probar que PhotoApiKey gana a ApiKey.
/// </summary>
public sealed class PhotoProxyFixture : ApiFixture
{
    public const string PhotoKey = "test-photo-key-SECRET";
    public const string IngestKey = "test-ingest-key-OTHER";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("GooglePlaces:PhotoApiKey", PhotoKey);
        builder.UseSetting("GooglePlaces:ApiKey", IngestKey);
        base.ConfigureWebHost(builder);
    }
}

/// <summary>Fixture con SOLO la ApiKey de ingesta → prueba el fallback de key.</summary>
public sealed class PhotoFallbackKeyFixture : ApiFixture
{
    public const string IngestKey = "test-ingest-only-FALLBACK";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("GooglePlaces:ApiKey", IngestKey);
        base.ConfigureWebHost(builder);
    }
}

/// <summary>
/// Fixture SIN ninguna key de Google (ni PhotoApiKey ni ApiKey) → degradación graceful a 404.
/// (La base ApiFixture ya no configura ninguna, pero lo dejamos explícito por claridad.)
/// </summary>
public sealed class PhotoNoKeyFixture : ApiFixture { }

/// <summary>Fixture con budget cap = 1 para observar el circuit breaker en un solo test.</summary>
public sealed class PhotoBudgetFixture : ApiFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("GooglePlaces:PhotoApiKey", "test-photo-key-budget");
        builder.UseSetting("GooglePlaces:PhotoDailyBudgetCap", "1");
        base.ConfigureWebHost(builder);
    }
}

/// <summary>
/// Fixture con rate-limiting REAL (PhotoLimit=2/min) + repoblado de IP de test, para verificar
/// que el techo por IP corta el endpoint de fotos.
/// </summary>
public sealed class PhotoRateLimitFixture : ApiFixture
{
    protected override bool DisableRateLimiting => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("GooglePlaces:PhotoApiKey", "test-photo-key-rl");
        builder.UseSetting("GooglePlaces:PhotoRateLimitPerMinute", "2");
        builder.ConfigureTestServices(s =>
            s.AddSingleton<IStartupFilter, TestClientIpStartupFilter>());
        base.ConfigureWebHost(builder);
    }
}

// ── Helpers ─────────────────────────────────────────────────────────────────────────

internal static class PhotoProxyTestData
{
    public static async Task<Guid> SeedPlaceWithGoogleId(ApiFixture fixture, string? googlePlaceId)
    {
        var db = fixture.GetDbContext();
        var id = Guid.NewGuid();
        db.Places.Add(new Place
        {
            Id = id,
            Name = $"Photo Place {id:N}",
            Category = "Food",
            City = "Miami",
            WhyThisPlace = "Great spot",
            Status = "published",
            GooglePlaceId = googlePlaceId,
        });
        await db.SaveChangesAsync();
        return id;
    }
}

// ── Camino feliz + seguridad de la key (PhotoApiKey wins) ─────────────────────────────

public class PhotoProxyHappyPathTests : IClassFixture<PhotoProxyFixture>
{
    private readonly PhotoProxyFixture _fixture;
    public PhotoProxyHappyPathTests(PhotoProxyFixture fixture)
    {
        _fixture = fixture;
        _fixture.FakePhotos.Reset();
    }

    [Fact]
    public async Task PlaceWithGoogleId_Returns302ToPhotoUri()
    {
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, "ChIJ_photo_happy");
        var client = _fixture.CreateNonRedirectingClient();

        var response = await client.GetAsync($"/places/{placeId}/photos/0");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); // 302
        Assert.Equal(FakePhotoHandler.DefaultPhotoUri, response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Response_NeverExposesApiKey_AndKeyGoesInHeaderServerSide()
    {
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, "ChIJ_photo_secure");
        var client = _fixture.CreateNonRedirectingClient();

        var response = await client.GetAsync($"/places/{placeId}/photos/0");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // (a) La key viaja server-side en el header X-Goog-Api-Key, y es la PhotoApiKey
        //     (gana a la ApiKey de ingesta cuando ambas están presentes).
        Assert.True(_fixture.FakePhotos.KeyHeaderSentWith(PhotoProxyFixture.PhotoKey));
        Assert.False(_fixture.FakePhotos.KeyHeaderSentWith(PhotoProxyFixture.IngestKey));

        // (b) La key NUNCA aparece en la respuesta al cliente: ni en Location, ni en ningún
        //     header, ni en el body.
        var location = response.Headers.Location?.ToString() ?? "";
        Assert.DoesNotContain(PhotoProxyFixture.PhotoKey, location);
        Assert.DoesNotContain(PhotoProxyFixture.IngestKey, location);

        var allHeaders = string.Join("\n",
            response.Headers.Concat(response.Content.Headers)
                .Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));
        Assert.DoesNotContain(PhotoProxyFixture.PhotoKey, allHeaders);
        Assert.DoesNotContain(PhotoProxyFixture.IngestKey, allHeaders);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(PhotoProxyFixture.PhotoKey, body);
        Assert.DoesNotContain(PhotoProxyFixture.IngestKey, body);
    }

    [Fact]
    public async Task MediaCall_UsesSkipHttpRedirect_AndKeyNotInQuery()
    {
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, "ChIJ_photo_query");
        var client = _fixture.CreateNonRedirectingClient();

        await client.GetAsync($"/places/{placeId}/photos/0");

        var mediaUri = _fixture.FakePhotos.LastMediaCall?.RequestUri?.ToString();
        Assert.NotNull(mediaUri);
        Assert.Contains("skipHttpRedirect=true", mediaUri);
        // La key va en HEADER, nunca en query (la query se loguearía).
        Assert.DoesNotContain("key=", mediaUri);
        Assert.DoesNotContain(PhotoProxyFixture.PhotoKey, mediaUri);
    }

    [Fact]
    public async Task Response_SetsNoStore_SoEphemeralRedirectIsNotCached()
    {
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, "ChIJ_photo_nocache");
        var client = _fixture.CreateNonRedirectingClient();

        var response = await client.GetAsync($"/places/{placeId}/photos/0");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");
    }
}

// ── Degradaciones a 404 ───────────────────────────────────────────────────────────────

public class PhotoProxyDegradationTests : IClassFixture<PhotoProxyFixture>
{
    private readonly PhotoProxyFixture _fixture;
    public PhotoProxyDegradationTests(PhotoProxyFixture fixture)
    {
        _fixture = fixture;
        _fixture.FakePhotos.Reset();
    }

    [Fact]
    public async Task NonexistentPlace_Returns404()
    {
        var client = _fixture.CreateNonRedirectingClient();
        var response = await client.GetAsync($"/places/{Guid.NewGuid()}/photos/0");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PlaceWithoutGoogleId_Returns404()
    {
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, googlePlaceId: null);
        var client = _fixture.CreateNonRedirectingClient();
        var response = await client.GetAsync($"/places/{placeId}/photos/0");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // Sin GooglePlaceId no debe llegarse a llamar a Google.
        Assert.Equal(0, _fixture.FakePhotos.DetailsCallCount);
        Assert.Equal(0, _fixture.FakePhotos.MediaCallCount);
    }

    [Fact]
    public async Task IndexOutOfRange_Returns404_AndDoesNotCallMedia()
    {
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, "ChIJ_oob");
        var client = _fixture.CreateNonRedirectingClient();

        // Solo hay una foto (index 0). Pedir index 5 → 404, sin llamada de pago /media.
        var response = await client.GetAsync($"/places/{placeId}/photos/5");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _fixture.FakePhotos.MediaCallCount);
    }

    [Fact]
    public async Task PlaceWithNoPhotos_Returns404()
    {
        _fixture.FakePhotos.ReturnNoPhotos = true;
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, "ChIJ_nophotos");
        var client = _fixture.CreateNonRedirectingClient();

        var response = await client.GetAsync($"/places/{placeId}/photos/0");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _fixture.FakePhotos.MediaCallCount);
    }

    [Fact]
    public async Task GoogleDetailsFails_Returns404_Not500()
    {
        _fixture.FakePhotos.DetailsResponder = _ =>
            FakePhotoHandler.Json("{\"error\":\"boom\"}", HttpStatusCode.InternalServerError);
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, "ChIJ_details_fail");
        var client = _fixture.CreateNonRedirectingClient();

        var response = await client.GetAsync($"/places/{placeId}/photos/0");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _fixture.FakePhotos.MediaCallCount);
    }

    [Fact]
    public async Task GoogleMediaFails_Returns404_Not500()
    {
        _fixture.FakePhotos.MediaResponder = _ =>
            FakePhotoHandler.Json("{\"error\":\"boom\"}", HttpStatusCode.BadGateway);
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, "ChIJ_media_fail");
        var client = _fixture.CreateNonRedirectingClient();

        var response = await client.GetAsync($"/places/{placeId}/photos/0");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NotFound_SetsNoStore_SoTransient404IsNotCached()
    {
        // Un 404 transitorio (aquí place inexistente, pero aplica a budget/errores de Google)
        // NO debe quedar cacheado en CDN/navegador y seguir suprimiendo la foto tras el reset.
        var client = _fixture.CreateNonRedirectingClient();

        var response = await client.GetAsync($"/places/{Guid.NewGuid()}/photos/0");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");
    }
}

// ── Allowlist de host del photoUri (defensa en profundidad) ───────────────────────────

public class PhotoProxyHostAllowlistTests : IClassFixture<PhotoProxyFixture>
{
    private readonly PhotoProxyFixture _fixture;
    public PhotoProxyHostAllowlistTests(PhotoProxyFixture fixture)
    {
        _fixture = fixture;
        _fixture.FakePhotos.Reset();
    }

    [Fact]
    public async Task PhotoUriOnGoogleSubdomain_Redirects302()
    {
        // Host por defecto = lh3.googleusercontent.com (subdominio) → permitido.
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, "ChIJ_allow_sub");
        var client = _fixture.CreateNonRedirectingClient();

        var response = await client.GetAsync($"/places/{placeId}/photos/0");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task PhotoUriOnGoogleApex_Redirects302()
    {
        _fixture.FakePhotos.PhotoUri = "https://googleusercontent.com/apex-hero.jpg";
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, "ChIJ_allow_apex");
        var client = _fixture.CreateNonRedirectingClient();

        var response = await client.GetAsync($"/places/{placeId}/photos/0");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://googleusercontent.com/apex-hero.jpg",
            response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task PhotoUriOnArbitraryHost_Returns404_DoesNotRedirect()
    {
        _fixture.FakePhotos.PhotoUri = "https://evil.example/x.jpg";
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, "ChIJ_deny_evil");
        var client = _fixture.CreateNonRedirectingClient();

        var response = await client.GetAsync($"/places/{placeId}/photos/0");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task PhotoUriWithGoogleHostAsPrefix_DoesNotEvadeAllowlist_Returns404()
    {
        // El host EMPIEZA por "googleusercontent.com" pero es un dominio del atacante. Un
        // check por substring lo dejaría pasar; el check por Host (termina en
        // ".googleusercontent.com" o es exacto) lo rechaza.
        _fixture.FakePhotos.PhotoUri = "https://googleusercontent.com.attacker.example/x.jpg";
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, "ChIJ_deny_prefix");
        var client = _fixture.CreateNonRedirectingClient();

        var response = await client.GetAsync($"/places/{placeId}/photos/0");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }
}

// ── Fallback de key ───────────────────────────────────────────────────────────────────

public class PhotoProxyFallbackKeyTests : IClassFixture<PhotoFallbackKeyFixture>
{
    private readonly PhotoFallbackKeyFixture _fixture;
    public PhotoProxyFallbackKeyTests(PhotoFallbackKeyFixture fixture)
    {
        _fixture = fixture;
        _fixture.FakePhotos.Reset();
    }

    [Fact]
    public async Task OnlyIngestKeyConfigured_UsesItAsFallback_Returns302()
    {
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, "ChIJ_fallback");
        var client = _fixture.CreateNonRedirectingClient();

        var response = await client.GetAsync($"/places/{placeId}/photos/0");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        // Con PhotoApiKey ausente, el server usa la ApiKey de ingesta (server-side, header).
        Assert.True(_fixture.FakePhotos.KeyHeaderSentWith(PhotoFallbackKeyFixture.IngestKey));
        // Y no se filtra en la respuesta.
        Assert.DoesNotContain(PhotoFallbackKeyFixture.IngestKey,
            response.Headers.Location?.ToString() ?? "");
    }
}

// ── Sin ninguna key ───────────────────────────────────────────────────────────────────

public class PhotoProxyNoKeyTests : IClassFixture<PhotoNoKeyFixture>
{
    private readonly PhotoNoKeyFixture _fixture;
    public PhotoProxyNoKeyTests(PhotoNoKeyFixture fixture)
    {
        _fixture = fixture;
        _fixture.FakePhotos.Reset();
    }

    [Fact]
    public async Task NoKeyConfigured_Returns404_AndNeverCallsGoogle()
    {
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, "ChIJ_nokey");
        var client = _fixture.CreateNonRedirectingClient();

        var response = await client.GetAsync($"/places/{placeId}/photos/0");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _fixture.FakePhotos.DetailsCallCount);
        Assert.Equal(0, _fixture.FakePhotos.MediaCallCount);
    }
}

// ── Circuit breaker de presupuesto ────────────────────────────────────────────────────

public class PhotoProxyBudgetBreakerTests : IClassFixture<PhotoBudgetFixture>
{
    private readonly PhotoBudgetFixture _fixture;
    public PhotoProxyBudgetBreakerTests(PhotoBudgetFixture fixture)
    {
        _fixture = fixture;
        _fixture.FakePhotos.Reset();
    }

    [Fact]
    public async Task CapReached_Returns404_StopsCallingMedia_AndResetsNextDay()
    {
        var placeId = await PhotoProxyTestData.SeedPlaceWithGoogleId(_fixture, "ChIJ_budget");
        var client = _fixture.CreateNonRedirectingClient();

        // Cap = 1: la primera request consume el presupuesto del día → 302 (1 llamada /media
        // + 1 llamada Details gratis).
        var first = await client.GetAsync($"/places/{placeId}/photos/0");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(1, _fixture.FakePhotos.MediaCallCount);
        Assert.Equal(1, _fixture.FakePhotos.DetailsCallCount);

        // La segunda, mismo día UTC: cap alcanzado → 404. El peek no-consumidor corta ANTES
        // de Details, así que NO se emite ni Details ni /media (ambos contadores se quedan).
        var second = await client.GetAsync($"/places/{placeId}/photos/0");
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(1, _fixture.FakePhotos.MediaCallCount);
        Assert.Equal(1, _fixture.FakePhotos.DetailsCallCount);

        // Avanzamos al día UTC siguiente → el contador se reinicia y vuelve a servir. El
        // conteo de /media sigue siendo exacto al coste (una por 302 real, ninguna por el 404).
        _fixture.FakeTime.Advance(TimeSpan.FromDays(1));
        var third = await client.GetAsync($"/places/{placeId}/photos/0");
        Assert.Equal(HttpStatusCode.Redirect, third.StatusCode);
        Assert.Equal(2, _fixture.FakePhotos.MediaCallCount);
        Assert.Equal(2, _fixture.FakePhotos.DetailsCallCount);
    }
}

// ── Rate limit (PhotoLimit por IP) ────────────────────────────────────────────────────

public class PhotoProxyRateLimitTests : IClassFixture<PhotoRateLimitFixture>
{
    private readonly PhotoRateLimitFixture _fixture;
    public PhotoProxyRateLimitTests(PhotoRateLimitFixture fixture)
    {
        _fixture = fixture;
        _fixture.FakePhotos.Reset();
    }

    [Fact]
    public async Task PhotoLimit_CapsRequestsPerIp_Returns429()
    {
        // Place inexistente da igual: el rate limiter corre en el pipeline ANTES del
        // controller, así que cada request cuenta contra el techo aunque acabe en 404.
        var missing = Guid.NewGuid();
        var client = _fixture.CreateNonRedirectingClient();
        client.DefaultRequestHeaders.Add("X-Test-Client-Ip", "77.0.0.1");

        // PhotoLimit = 2/min: las dos primeras NO son 429.
        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await client.GetAsync($"/places/{missing}/photos/0")).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await client.GetAsync($"/places/{missing}/photos/0")).StatusCode);
        // La tercera se rechaza con 429.
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.GetAsync($"/places/{missing}/photos/0")).StatusCode);
    }

    [Fact]
    public async Task PhotoLimit_IsPerIp_OtherIpNotBlocked()
    {
        var missing = Guid.NewGuid();

        var flooder = _fixture.CreateNonRedirectingClient();
        flooder.DefaultRequestHeaders.Add("X-Test-Client-Ip", "77.0.0.2");
        await flooder.GetAsync($"/places/{missing}/photos/0");
        await flooder.GetAsync($"/places/{missing}/photos/0");
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await flooder.GetAsync($"/places/{missing}/photos/0")).StatusCode);

        // Otra IP mantiene su propio bucket.
        var other = _fixture.CreateNonRedirectingClient();
        other.DefaultRequestHeaders.Add("X-Test-Client-Ip", "77.0.0.3");
        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await other.GetAsync($"/places/{missing}/photos/0")).StatusCode);
    }
}
