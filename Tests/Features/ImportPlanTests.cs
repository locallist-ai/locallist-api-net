using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;

namespace LocalList.API.Tests.Features;

/// <summary>
/// F2 T4 — <c>POST /import/plan</c> sobre DB real (ApiFixture = Testcontainers PostgreSQL). Crea el
/// plan a partir de un import confirmado: gate Plus + auth, validación atómica/opaca de places,
/// gating de terceros, clamp de días, dedup, atribución saneada, source=imported (nunca curated/
/// showcase), naming bilingüe y DETERMINISMO del scheduling. La DB nunca se mockea.
/// </summary>
public class ImportPlanTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // ── helpers ──────────────────────────────────────────────────────────────────

    private async Task<(HttpClient client, Guid userId)> PlusClient(string tag = "impl")
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(uid, $"{tag}-{uid:N}@test.com", tier: "pro");
        return (client, uid);
    }

    private static string LiveCity()
    {
        var c = "TestCity" + Guid.NewGuid().ToString("N")[..10];
        return c;
    }

    /// <summary>Siembra <paramref name="n"/> places PUBLICADOS en <paramref name="city"/> y marca la ciudad LIVE.</summary>
    private async Task<List<Guid>> SeedLive(string city, int n)
    {
        fixture.MarkCityLive(city);
        return await Seed(city, n, "published");
    }

    private async Task<List<Guid>> Seed(string city, int n, string status, JsonDocument? hours = null)
    {
        var db = fixture.GetDbContext();
        var ids = new List<Guid>();
        for (var i = 0; i < n; i++)
        {
            var id = Guid.NewGuid();
            db.Places.Add(new Place
            {
                Id = id,
                Name = $"Place-{i}-{Guid.NewGuid():N}"[..16],
                City = city,
                Status = status,
                Category = "culture",
                WhyThisPlace = "t",
                Latitude = 25.76m + i * 0.002m,
                Longitude = -80.19m + i * 0.002m,
                OpeningHours = hours,
            });
            ids.Add(id);
        }
        await db.SaveChangesAsync();
        return ids;
    }

    private static Task<HttpResponseMessage> Post(
        HttpClient client, string city, int days, IEnumerable<Guid> ids,
        string? planName = null, string? platform = null, string? creatorHandle = null, string? acceptLang = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/import/plan");
        if (acceptLang is not null) req.Headers.Add("Accept-Language", acceptLang);
        req.Content = JsonContent.Create(new { city, days, placeIds = ids, planName, platform, creatorHandle });
        return client.SendAsync(req);
    }

    private static int TotalStops(JsonElement body) =>
        body.GetProperty("days").EnumerateArray().Sum(d => d.GetProperty("stops").GetArrayLength());

    private static List<(Guid place, int day, int order)> StopTuples(JsonElement body) =>
        body.GetProperty("days").EnumerateArray()
            .SelectMany(d => d.GetProperty("stops").EnumerateArray()
                .Select(s => (
                    s.GetProperty("placeId").GetGuid(),
                    s.GetProperty("dayNumber").GetInt32(),
                    s.GetProperty("orderIndex").GetInt32())))
            .ToList();

    // ── (a) happy path ────────────────────────────────────────────────────────────
    [Fact]
    public async Task Plus_FiveMatchedPlaces_Creates201_PrivateImportedPlan_WithAttribution()
    {
        var (client, userId) = await PlusClient();
        var city = LiveCity();
        var ids = await SeedLive(city, 5);

        var res = await Post(client, city, 2, ids, platform: "self", creatorHandle: "@chef");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        // Todos los 5 placeIds repartidos en días.
        Assert.Equal(5, TotalStops(body));
        Assert.Equal(city, body.GetProperty("city").GetString());
        Assert.Equal(2, body.GetProperty("durationDays").GetInt32());
        Assert.False(body.GetProperty("isPublic").GetBoolean());

        // Atribución en el DTO: handle presente; platform=self NO se expone (null → omitido).
        Assert.Equal("chef", body.GetProperty("importedCreatorHandle").GetString());
        Assert.False(body.TryGetProperty("importedFromPlatform", out _));

        var planId = body.GetProperty("id").GetGuid();
        var db = fixture.GetDbContext();
        var plan = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == planId);
        Assert.Equal("imported", plan.Source);
        Assert.Equal("private", plan.Visibility);
        Assert.False(plan.IsShowcase);
        Assert.Equal(userId, plan.CreatedById);
        Assert.Equal("chef", plan.ImportedCreatorHandle);
        Assert.Null(plan.ImportedFromPlatform);

        var stops = await db.PlanStops.AsNoTracking().Where(s => s.PlanId == planId).ToListAsync();
        Assert.Equal(5, stops.Count);
        Assert.Equal(ids.OrderBy(x => x), stops.Select(s => s.PlaceId).OrderBy(x => x));
    }

    // ── (a2) invariante no-loss: un place inviable (cerrado) se RECONCILIA, no se pierde ──
    [Fact]
    public async Task PlaceDroppedByViability_IsReconciled_NotLost()
    {
        var (client, _) = await PlusClient("impl-recon");
        var city = LiveCity();
        var ids = await SeedLive(city, 3);
        // 4º place con horario VACÍO (cerrado siempre) → el walk-clock lo descarta por viabilidad.
        var closed = new OpeningHoursData(new List<OpeningPeriod>(), new List<string>()).ToJsonDocument();
        var closedId = (await Seed(city, 1, "published", closed))[0];
        ids.Add(closedId);

        var res = await Post(client, city, 1, ids, platform: "self");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        // Los 4 confirmados están, incluido el inviable (reconciliado sin horario).
        Assert.Equal(4, TotalStops(body));
        var tuples = StopTuples(body);
        Assert.Contains(tuples, t => t.place == closedId);
    }

    // ── (b) free → 403; anónimo → 401 ────────────────────────────────────────────
    [Fact]
    public async Task FreeUser_Returns403RequiresPlus()
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(uid, $"impl-free-{uid:N}@test.com", tier: "free");
        var city = LiveCity();
        var ids = await SeedLive(city, 2);

        var res = await Post(client, city, 1, ids);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("import_requires_plus", body.GetProperty("error").GetString());
        Assert.Equal(0, await fixture.GetDbContext().Plans.CountAsync(p => p.CreatedById == uid));
    }

    [Fact]
    public async Task Anonymous_Returns401()
    {
        var client = fixture.CreateClient();
        var res = await Post(client, "Miami", 1, new[] { Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ── (c) placeId inexistente / draft / de otra ciudad → 400 opaco, 0 filas (atómico) ──
    [Theory]
    [InlineData("nonexistent")]
    [InlineData("draft")]
    [InlineData("othercity")]
    public async Task InvalidPlaces_Returns400_NoPlanCreated(string kind)
    {
        var (client, userId) = await PlusClient("impl-inv");
        var city = LiveCity();
        var ids = await SeedLive(city, 3);

        switch (kind)
        {
            case "nonexistent":
                ids.Add(Guid.NewGuid());
                break;
            case "draft":
                ids.Add((await Seed(city, 1, "draft"))[0]);
                break;
            case "othercity":
                var other = LiveCity();
                ids.Add((await Seed(other, 1, "published"))[0]);
                break;
        }

        var res = await Post(client, city, 2, ids);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("import_invalid_places", body.GetProperty("error").GetString());

        // Atómico: no se creó plan ni stops para este usuario.
        var db = fixture.GetDbContext();
        Assert.Equal(0, await db.Plans.CountAsync(p => p.CreatedById == userId));
    }

    [Fact]
    public async Task EmptyPlaceIds_Returns400()
    {
        var (client, _) = await PlusClient("impl-empty");
        var res = await Post(client, "Miami", 1, Array.Empty<Guid>());
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("import_invalid_places", body.GetProperty("error").GetString());
    }

    // ── (c') ciudad no cubierta → 400 city_unsupported ───────────────────────────
    [Fact]
    public async Task CityNotCovered_Returns400CityUnsupported()
    {
        var (client, userId) = await PlusClient("impl-nocov");
        var city = "TestCity" + Guid.NewGuid().ToString("N")[..10]; // NO marcada live
        var ids = await Seed(city, 2, "published");

        var res = await Post(client, city, 1, ids);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("city_unsupported", body.GetProperty("error").GetString());
        Assert.Equal(0, await fixture.GetDbContext().Plans.CountAsync(p => p.CreatedById == userId));
    }

    // ── (d) platform=tiktok con flag OFF → 403 third_party_import_disabled ────────
    [Fact]
    public async Task ThirdPartyPlatform_FlagOff_Returns403()
    {
        var (client, userId) = await PlusClient("impl-3p");
        var city = LiveCity();
        var ids = await SeedLive(city, 2);

        var res = await Post(client, city, 1, ids, platform: "tiktok", creatorHandle: "@creator");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("third_party_import_disabled", body.GetProperty("error").GetString());
        Assert.Equal(0, await fixture.GetDbContext().Plans.CountAsync(p => p.CreatedById == userId));
    }

    // ── (e) handle malicioso → saneado a null, plan creado, NUNCA persistido sucio ──
    [Theory]
    [InlineData("http://evil.com/x")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task MaliciousHandle_SanitizedToNull_PlanStillCreated(string handle)
    {
        var (client, _) = await PlusClient("impl-mal");
        var city = LiveCity();
        var ids = await SeedLive(city, 2);

        var res = await Post(client, city, 1, ids, platform: "self", creatorHandle: handle);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.TryGetProperty("importedCreatorHandle", out _)); // null → omitido

        var planId = body.GetProperty("id").GetGuid();
        var plan = await fixture.GetDbContext().Plans.AsNoTracking().FirstAsync(p => p.Id == planId);
        Assert.Null(plan.ImportedCreatorHandle);
    }

    // ── (f) days clamp + dedup ───────────────────────────────────────────────────
    [Fact]
    public async Task DaysClampedToMax_And_Min()
    {
        var (client, _) = await PlusClient("impl-clamp");
        var city = LiveCity();
        var ids = await SeedLive(city, 2);

        var over = await Post(client, city, 99, ids);
        Assert.Equal(HttpStatusCode.Created, over.StatusCode);
        Assert.Equal(14, (await over.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("durationDays").GetInt32());

        var under = await Post(client, city, 0, ids);
        Assert.Equal(HttpStatusCode.Created, under.StatusCode);
        Assert.Equal(1, (await under.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("durationDays").GetInt32());
    }

    [Fact]
    public async Task DuplicatePlaceIds_AreDeduped()
    {
        var (client, _) = await PlusClient("impl-dedup");
        var city = LiveCity();
        var ids = await SeedLive(city, 3);
        var withDupes = ids.Concat(ids).ToList(); // cada id repetido

        var res = await Post(client, city, 1, withDupes);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, TotalStops(body)); // 3 únicos, no 6
    }

    // ── (g) imported NUNCA es curated ni showcase ────────────────────────────────
    [Fact]
    public async Task ImportedPlan_IsNeitherCuratedNorShowcase()
    {
        var (client, _) = await PlusClient("impl-cur");
        var city = LiveCity();
        var ids = await SeedLive(city, 2);

        var res = await Post(client, city, 1, ids);
        var planId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var plan = await fixture.GetDbContext().Plans.AsNoTracking().FirstAsync(p => p.Id == planId);
        Assert.NotEqual("curated", plan.Source);
        Assert.False(plan.IsShowcase);

        // No aparece en el feed público/showcase (private + no showcase).
        var anon = fixture.CreateClient();
        var list = await (await anon.GetAsync($"/plans?city={city}&showcase=true")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, list.GetProperty("total").GetInt32());
    }

    // ── (h) naming: sin planName + Accept-Language:es → nombre ES del fallback ─────
    [Fact]
    public async Task NoPlanName_SpanishLang_UsesLocalizedFallback()
    {
        var (client, _) = await PlusClient("impl-name");
        var city = LiveCity();
        var ids = await SeedLive(city, 2);

        var res = await Post(client, city, 2, ids, acceptLang: "es-ES");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"Plan a medida de 2 días en {city}", body.GetProperty("name").GetString());
    }

    // ── (h2) planName con em-dash → saneado ──────────────────────────────────────
    [Fact]
    public async Task PlanNameWithEmDash_IsSanitized()
    {
        var (client, _) = await PlusClient("impl-emdash");
        var city = LiveCity();
        var ids = await SeedLive(city, 2);

        var res = await Post(client, city, 1, ids, planName: "Miami — sunset");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var name = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString();
        Assert.DoesNotContain("—", name);
        Assert.Contains("Miami", name);
    }

    // ── TOCTOU hard-delete: predicado del catch del SaveChanges ─────────────────
    [Fact]
    public void SaveCatchPredicate_ClassifiesFkViolation()
    {
        // La carrera real (hard-delete admin de un place ENTRE el SELECT de validación y el
        // INSERT de los stops) no es reproducible determinísticamente vía ApiFixture — exigiría
        // pausar la request a mitad del action. Se testea el predicado que enruta el catch de
        // ImportPlanController (COMPARTIDO con Favorites, misma carrera): 23503 → 400
        // import_invalid_places (no 500); cualquier otro DbUpdateException → propaga.
        var fk = new DbUpdateException("insert failed",
            new Npgsql.PostgresException("violates foreign key \"FK_plan_stops_places_place_id\"", "ERROR", "ERROR", "23503"));
        var other = new DbUpdateException("boom", new InvalidOperationException("connection lost"));

        Assert.True(LocalList.API.NET.Features.Favorites.FavoritesController.IsForeignKeyViolation(fk));
        Assert.False(LocalList.API.NET.Features.Favorites.FavoritesController.IsForeignKeyViolation(other));
    }

    // ── (i) determinismo: mismo SET → misma secuencia de stops, aunque la 2ª llamada
    //        envíe los placeIds BARAJADOS (el plan es función del set, no del orden de envío) ──
    [Fact]
    public async Task SameInput_EvenShuffled_ProducesDeterministicSchedule()
    {
        var (client, _) = await PlusClient("impl-det");
        var city = LiveCity();
        var ids = await SeedLive(city, 6);
        var shuffled = ids.AsEnumerable().Reverse().ToList();
        Assert.NotEqual(ids, shuffled); // el shuffle es real

        var a = await (await Post(client, city, 3, ids)).Content.ReadFromJsonAsync<JsonElement>();
        var b = await (await Post(client, city, 3, shuffled)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(StopTuples(a), StopTuples(b));
    }
}

/// <summary>
/// Fixture con el rate limiting REAL activo y <c>Import:RateLimitPerHourPerIp=2</c> — verifica que
/// <c>POST /import/plan</c> comparte el techo por IP <c>ImportLimit</c> con T1 (este endpoint
/// acepta placeIds published arbitrarios SIN exigir un import previo, así que sin el techo solo lo
/// acotaría el global 100/min). Reusa el <see cref="TestClientIpStartupFilter"/> de Builder para
/// fijar la IP. Container propio (config distinta).
/// </summary>
public sealed class ImportPlanRateLimitFixture : ApiFixture
{
    protected override bool DisableRateLimiting => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Import:RateLimitPerHourPerIp", "2");
        builder.ConfigureTestServices(s =>
            s.AddSingleton<IStartupFilter, TestClientIpStartupFilter>());
        base.ConfigureWebHost(builder);
    }
}

public class ImportPlanRateLimitTests(ImportPlanRateLimitFixture fixture)
    : IClassFixture<ImportPlanRateLimitFixture>
{
    [Fact]
    public async Task ImportPlan_SharesImportLimitIpCeiling_ThirdRequestReturns429()
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(uid, $"implrl-{uid:N}@test.com", tier: "pro");
        var city = "TestCity" + Guid.NewGuid().ToString("N")[..10];
        fixture.MarkCityLive(city);

        var db = fixture.GetDbContext();
        var ids = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var id = Guid.NewGuid();
            db.Places.Add(new Place
            {
                Id = id, Name = $"RL{i}-{Guid.NewGuid():N}"[..12], City = city,
                Status = "published", Category = "food", WhyThisPlace = "t",
            });
            ids.Add(id);
        }
        await db.SaveChangesAsync();

        async Task<HttpResponseMessage> Send()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/import/plan")
            {
                Content = JsonContent.Create(new { city, days = 1, placeIds = ids })
            };
            req.Headers.Add("X-Test-Client-Ip", "10.99.42.7"); // misma IP → mismo bucket ImportLimit
            return await client.SendAsync(req);
        }

        Assert.Equal(HttpStatusCode.Created, (await Send()).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send()).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send()).StatusCode); // techo 2/hr por IP
    }
}

/// <summary>
/// F2 T4 con <c>Import:ThirdPartyEnabled=true</c>: platform de terceros crea el plan y persiste la
/// atribución (plataforma + handle). Container propio (config distinta).
/// </summary>
public class ImportPlanThirdPartyTests(ImportThirdPartyEnabledFixture fixture)
    : IClassFixture<ImportThirdPartyEnabledFixture>
{
    [Fact]
    public async Task ThirdPartyPlatform_FlagOn_CreatesPlan_WithPlatformAndHandle()
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(uid, $"impl3p-{uid:N}@test.com", tier: "pro");
        var city = "TestCity" + Guid.NewGuid().ToString("N")[..10];
        fixture.MarkCityLive(city);

        var db = fixture.GetDbContext();
        var ids = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var id = Guid.NewGuid();
            db.Places.Add(new Place
            {
                Id = id, Name = $"P{i}-{Guid.NewGuid():N}"[..12], City = city,
                Status = "published", Category = "food", WhyThisPlace = "t",
            });
            ids.Add(id);
        }
        await db.SaveChangesAsync();

        var req = new HttpRequestMessage(HttpMethod.Post, "/import/plan")
        {
            Content = JsonContent.Create(new
            {
                city, days = 1, placeIds = ids, platform = "tiktok", creatorHandle = "@creator"
            })
        };
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("tiktok", body.GetProperty("importedFromPlatform").GetString());
        Assert.Equal("creator", body.GetProperty("importedCreatorHandle").GetString());

        var planId = body.GetProperty("id").GetGuid();
        var plan = await fixture.GetDbContext().Plans.AsNoTracking().FirstAsync(p => p.Id == planId);
        Assert.Equal("tiktok", plan.ImportedFromPlatform);
        Assert.Equal("creator", plan.ImportedCreatorHandle);
        Assert.Equal("imported", plan.Source);
    }
}
