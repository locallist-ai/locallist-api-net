using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Usage;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.Tests.Features;

/// <summary>
/// POST /plans/{id}/clone — "guardar este plan como mío". Clona un showcase curado o un plan
/// público a la cuenta del caller como plan PRIVADO propio (source="cloned"). DB real
/// (Testcontainers): nada se mockea.
/// </summary>
public class PlanCloneTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<Guid> SeedUser(string tag, string tier = "free")
    {
        var id = Guid.NewGuid();
        var db = fixture.GetDbContext();
        db.Users.Add(new User
        {
            Id = id,
            Email = $"{tag}-{id:N}@test.com",
            FirebaseUid = $"fb-{tag}-{id:N}",
            Tier = tier,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private Task<HttpClient> AppClient(Guid userId, string tag) =>
        fixture.CreateAppAuthenticatedClientWithUser(userId, $"{tag}-{userId:N}@test.com");

    private async Task<Place> SeedPlace(string name)
    {
        var db = fixture.GetDbContext();
        var place = new Place
        {
            Id = Guid.NewGuid(), Name = name, Category = "Food",
            WhyThisPlace = "Nice", Status = "published",
        };
        db.Places.Add(place);
        await db.SaveChangesAsync();
        return place;
    }

    /// <summary>Siembra un plan con N stops repartidos en días/orden concretos.</summary>
    private async Task<(Plan plan, List<Guid> placeIds)> SeedPlan(
        string name, string visibility, bool isShowcase, string source = "curated",
        Guid? ownerId = null, int stopCount = 3,
        JsonDocument? nameI18n = null, JsonDocument? descriptionI18n = null,
        JsonDocument? translationStatus = null,
        string? imageUrl = null, DateOnly? startDate = null, string? tripContextJson = null)
    {
        var db = fixture.GetDbContext();
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = name,
            City = "Miami",
            Type = "curated",
            Source = source,
            DurationDays = 2,
            Visibility = visibility,
            IsShowcase = isShowcase,
            CreatedById = ownerId,
            NameI18n = nameI18n,
            DescriptionI18n = descriptionI18n,
            TranslationStatus = translationStatus,
            ImageUrl = imageUrl,
            StartDate = startDate,
            TripContext = tripContextJson is null ? null : JsonDocument.Parse(tripContextJson),
        };
        db.Plans.Add(plan);

        var placeIds = new List<Guid>(stopCount);
        for (var i = 0; i < stopCount; i++)
        {
            var place = new Place
            {
                Id = Guid.NewGuid(), Name = $"{name} Stop {i}", Category = "Food",
                WhyThisPlace = "Nice", Status = "published",
            };
            db.Places.Add(place);
            placeIds.Add(place.Id);
            db.PlanStops.Add(new PlanStop
            {
                Id = Guid.NewGuid(),
                PlanId = plan.Id,
                PlaceId = place.Id,
                DayNumber = (i / 2) + 1,
                OrderIndex = i,
                TimeBlock = i % 2 == 0 ? "morning" : "afternoon",
                SuggestedArrival = TimeSpan.FromHours(9 + i),
                SuggestedDurationMin = 60 + i,
            });
        }
        await db.SaveChangesAsync();
        return (plan, placeIds);
    }

    // ── (a) Clonar un showcase → plan privado propio con TODOS los stops copiados ──
    [Fact]
    public async Task Clone_Showcase_CreatesPrivateOwnedCopy_WithAllStops()
    {
        var caller = await SeedUser("cl-owner");
        var (source, placeIds) = await SeedPlan("Showcase Weekend", "public", isShowcase: true, stopCount: 3);
        var client = await AppClient(caller, "cl-owner-c");

        var res = await client.PostAsync($"/plans/{source.Id}/clone", null);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var cloneId = body.GetProperty("id").GetGuid();
        Assert.NotEqual(source.Id, cloneId);

        // DTO público: privado propio, nunca showcase.
        Assert.False(body.GetProperty("isPublic").GetBoolean());
        Assert.False(body.GetProperty("isShowcase").GetBoolean());
        Assert.Equal(caller, body.GetProperty("createdById").GetGuid());

        // Fuente de verdad en DB.
        var db = fixture.GetDbContext();
        var clone = await db.Plans.AsNoTracking().Include(p => p.Stops).FirstAsync(p => p.Id == cloneId);
        Assert.Equal("private", clone.Visibility);
        Assert.False(clone.IsPublic);
        Assert.Equal("cloned", clone.Source);
        Assert.False(clone.IsShowcase);
        Assert.Equal(source.Id, clone.ClonedFrom);
        Assert.Equal(caller, clone.CreatedById);
        Assert.Equal(source.Name, clone.Name);
        Assert.Equal(source.DurationDays, clone.DurationDays);

        // Copia fiel de stops: mismo nº, nuevos ids de stop, place_ids/orden/día correctos.
        var srcStops = await db.PlanStops.AsNoTracking().Where(s => s.PlanId == source.Id)
            .OrderBy(s => s.OrderIndex).ToListAsync();
        var cloneStops = clone.Stops.OrderBy(s => s.OrderIndex).ToList();
        Assert.Equal(srcStops.Count, cloneStops.Count);
        for (var i = 0; i < srcStops.Count; i++)
        {
            Assert.NotEqual(srcStops[i].Id, cloneStops[i].Id);       // nuevo id de stop
            Assert.Equal(cloneId, cloneStops[i].PlanId);
            Assert.Equal(srcStops[i].PlaceId, cloneStops[i].PlaceId);
            Assert.Equal(srcStops[i].DayNumber, cloneStops[i].DayNumber);
            Assert.Equal(srcStops[i].OrderIndex, cloneStops[i].OrderIndex);
            Assert.Equal(srcStops[i].TimeBlock, cloneStops[i].TimeBlock);
            Assert.Equal(srcStops[i].SuggestedArrival, cloneStops[i].SuggestedArrival);
            Assert.Equal(srcStops[i].SuggestedDurationMin, cloneStops[i].SuggestedDurationMin);
        }
        Assert.Equal(placeIds.OrderBy(x => x), cloneStops.Select(s => s.PlaceId).OrderBy(x => x));
    }

    // ── (b) El clon NO aparece en GET /plans?showcase=true ni se trata como curated ──
    [Fact]
    public async Task Clone_DoesNotAppearInShowcaseList_NorTreatedAsCurated()
    {
        var caller = await SeedUser("cl-noshow");
        var (source, _) = await SeedPlan("Showcase Src", "public", isShowcase: true, stopCount: 1);
        var client = await AppClient(caller, "cl-noshow-c");

        var res = await client.PostAsync($"/plans/{source.Id}/clone", null);
        var cloneId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // El listado showcase anónimo no incluye el clon (privado + no-showcase).
        var listBody = await (await fixture.CreateClient().GetAsync("/plans?showcase=true"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var showcaseIds = listBody.GetProperty("plans").EnumerateArray()
            .Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(cloneId, showcaseIds);

        // No es curated: source="cloned" => isCurated=false en el DTO (idioma se resuelve como user plan).
        var db = fixture.GetDbContext();
        var clone = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == cloneId);
        Assert.NotEqual("curated", clone.Source);
    }

    // ── (c) Clonar plan privado AJENO → 404; inexistente → 404 ──
    [Fact]
    public async Task Clone_OtherUsersPrivatePlan_Returns404()
    {
        var otherOwner = await SeedUser("cl-otherowner");
        var caller = await SeedUser("cl-caller-priv");
        var (source, _) = await SeedPlan(
            "Private Foreign", "private", isShowcase: false, source: "user",
            ownerId: otherOwner, stopCount: 1);
        var client = await AppClient(caller, "cl-caller-priv-c");

        var res = await client.PostAsync($"/plans/{source.Id}/clone", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        // Nada creado.
        var db = fixture.GetDbContext();
        Assert.False(await db.Plans.AnyAsync(p => p.CreatedById == caller));
    }

    [Fact]
    public async Task Clone_UnlistedPlan_Returns404()
    {
        var otherOwner = await SeedUser("cl-unlisted-owner");
        var caller = await SeedUser("cl-unlisted-caller");
        var (source, _) = await SeedPlan(
            "Unlisted Foreign", "unlisted", isShowcase: false, source: "user",
            ownerId: otherOwner, stopCount: 1);
        var client = await AppClient(caller, "cl-unlisted-caller-c");

        var res = await client.PostAsync($"/plans/{source.Id}/clone", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Clone_NonExistentPlan_Returns404()
    {
        var caller = await SeedUser("cl-missing");
        var client = await AppClient(caller, "cl-missing-c");

        var res = await client.PostAsync($"/plans/{Guid.NewGuid()}/clone", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ── (d) Free en el cap → 403 saved_plans_limit_reached; pro ok ──
    [Fact]
    public async Task Clone_FreeUser_AtSavedLimit_Returns403()
    {
        var caller = await SeedUser("cl-cap-free");
        // Llena el cupo con planes propios (no clones de este origen, para no colisionar con idempotencia).
        var db = fixture.GetDbContext();
        for (var i = 0; i < PlanGenerationGateService.FreeSavedPlansLimit; i++)
            db.Plans.Add(new Plan
            {
                Id = Guid.NewGuid(), Name = $"Saved {i}", City = "Miami", Type = "custom",
                IsPublic = false, CreatedById = caller,
            });
        await db.SaveChangesAsync();

        var (source, _) = await SeedPlan("Showcase Cap", "public", isShowcase: true, stopCount: 1);
        var client = await AppClient(caller, "cl-cap-free-c");

        var res = await client.PostAsync($"/plans/{source.Id}/clone", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("saved_plans_limit_reached", body.GetProperty("error").GetString());
        Assert.Equal(PlanGenerationGateService.FreeSavedPlansLimit, body.GetProperty("limit").GetInt32());

        // No se creó el clon.
        Assert.False(await fixture.GetDbContext().Plans
            .AnyAsync(p => p.CreatedById == caller && p.ClonedFrom == source.Id));
    }

    [Fact]
    public async Task Clone_ProUser_OverSavedLimit_Succeeds()
    {
        var caller = await SeedUser("cl-cap-pro", tier: "pro");
        var db = fixture.GetDbContext();
        for (var i = 0; i < PlanGenerationGateService.FreeSavedPlansLimit + 1; i++)
            db.Plans.Add(new Plan
            {
                Id = Guid.NewGuid(), Name = $"Pro Saved {i}", City = "Miami", Type = "custom",
                IsPublic = false, CreatedById = caller,
            });
        await db.SaveChangesAsync();

        var (source, _) = await SeedPlan("Showcase Pro Cap", "public", isShowcase: true, stopCount: 1);
        var client = await AppClient(caller, "cl-cap-pro-c");

        var res = await client.PostAsync($"/plans/{source.Id}/clone", null);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    public async Task Clone_NewUser_FirstClone_NotCapped()
    {
        var caller = await SeedUser("cl-first");
        var (source, _) = await SeedPlan("Showcase First", "public", isShowcase: true, stopCount: 1);
        var client = await AppClient(caller, "cl-first-c");

        var res = await client.PostAsync($"/plans/{source.Id}/clone", null);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    // ── (e) Anónimo → 401 ──
    [Fact]
    public async Task Clone_Anonymous_Returns401()
    {
        var (source, _) = await SeedPlan("Showcase Anon", "public", isShowcase: true, stopCount: 1);
        var client = fixture.CreateClient();

        var res = await client.PostAsync($"/plans/{source.Id}/clone", null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ── (f) Idempotencia: doble clone del mismo origen → mismo plan (no duplica) ──
    [Fact]
    public async Task Clone_Twice_SameSource_ReturnsSamePlan_NoDuplicate()
    {
        var caller = await SeedUser("cl-idem");
        var (source, _) = await SeedPlan("Showcase Idem", "public", isShowcase: true, stopCount: 2);
        var client = await AppClient(caller, "cl-idem-c");

        var first = await client.PostAsync($"/plans/{source.Id}/clone", null);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var second = await client.PostAsync($"/plans/{source.Id}/clone", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // 200 = devuelve el existente
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(firstId, secondId);

        var db = fixture.GetDbContext();
        Assert.Equal(1, await db.Plans.CountAsync(p => p.CreatedById == caller && p.ClonedFrom == source.Id));
    }

    // ── (g) i18n: showcase con ES → el clon conserva el ES ──
    [Fact]
    public async Task Clone_ShowcaseWithSpanish_PreservesSpanish()
    {
        var caller = await SeedUser("cl-i18n");
        var nameI18n = JsonDocument.Parse("""{"en":"Weekend in Miami","es":"Fin de semana en Miami"}""");
        var descI18n = JsonDocument.Parse("""{"en":"A curated weekend.","es":"Un fin de semana curado."}""");
        var status = JsonDocument.Parse("""{"es":"approved"}""");
        var (source, _) = await SeedPlan(
            "Weekend in Miami", "public", isShowcase: true, stopCount: 1,
            nameI18n: nameI18n, descriptionI18n: descI18n, translationStatus: status);
        var client = await AppClient(caller, "cl-i18n-c");

        var cloneRes = await client.PostAsync($"/plans/{source.Id}/clone", null);
        Assert.Equal(HttpStatusCode.Created, cloneRes.StatusCode);
        var cloneId = (await cloneRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // GET del clon con Accept-Language: es → conserva el ES aunque source!="curated" (no exige approved).
        var req = new HttpRequestMessage(HttpMethod.Get, $"/plans/{cloneId}");
        req.Headers.Add("Accept-Language", "es");
        var getBody = await (await client.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Fin de semana en Miami", getBody.GetProperty("name").GetString());
        Assert.Equal("Un fin de semana curado.", getBody.GetProperty("description").GetString());

        // i18n persistido en el clon.
        var db = fixture.GetDbContext();
        var clone = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == cloneId);
        Assert.NotNull(clone.NameI18n);
        Assert.Equal("Fin de semana en Miami", clone.NameI18n!.RootElement.GetProperty("es").GetString());
    }

    // ── (h) El caller puede editar/borrar/follow su clon (flujo dueño normal) ──
    [Fact]
    public async Task Clone_OwnerCanEditAndDelete_TheirClone()
    {
        var caller = await SeedUser("cl-owns");
        var (source, _) = await SeedPlan("Showcase Owns", "public", isShowcase: true, stopCount: 1);
        var client = await AppClient(caller, "cl-owns-c");

        var cloneId = (await (await client.PostAsync($"/plans/{source.Id}/clone", null))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Editar stops (CanEdit del owner).
        var newPlace = await SeedPlace("New Edit Place");
        var edit = await client.PutAsJsonAsync($"/plans/{cloneId}/stops", new
        {
            stops = new[] { new { placeId = newPlace.Id, dayNumber = 1, orderIndex = 0, timeBlock = "morning" } }
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        // Borrar (IsOwner).
        var del = await client.DeleteAsync($"/plans/{cloneId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var db = fixture.GetDbContext();
        Assert.False(await db.Plans.AnyAsync(p => p.Id == cloneId));
    }

    // ── Clonar un plan PÚBLICO de usuario (no showcase) también funciona ──
    [Fact]
    public async Task Clone_PublicUserPlan_Succeeds()
    {
        var otherOwner = await SeedUser("cl-pub-owner");
        var caller = await SeedUser("cl-pub-caller");
        var (source, _) = await SeedPlan(
            "Public User Plan", "public", isShowcase: false, source: "user",
            ownerId: otherOwner, stopCount: 2);
        var client = await AppClient(caller, "cl-pub-caller-c");

        var res = await client.PostAsync($"/plans/{source.Id}/clone", null);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var cloneId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var db = fixture.GetDbContext();
        var clone = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == cloneId);
        Assert.Equal(caller, clone.CreatedById);
        Assert.Equal("private", clone.Visibility);
        Assert.Equal(source.Id, clone.ClonedFrom);
    }

    // ── MINOR ImageUrl: el clon conserva la imagen del plan origen ──
    [Fact]
    public async Task Clone_Showcase_PreservesImageUrl()
    {
        var caller = await SeedUser("cl-img");
        var (source, _) = await SeedPlan(
            "Showcase Image", "public", isShowcase: true, stopCount: 1,
            imageUrl: "https://example.com/hero.jpg");
        var client = await AppClient(caller, "cl-img-c");

        var res = await client.PostAsync($"/plans/{source.Id}/clone", null);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var cloneId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var db = fixture.GetDbContext();
        var clone = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == cloneId);
        Assert.Equal("https://example.com/hero.jpg", clone.ImageUrl);
    }

    // ── MINOR TripContext (privacidad): showcase curado → se copia; plan público ajeno → null ──
    [Fact]
    public async Task Clone_Showcase_KeepsTripContext()
    {
        var caller = await SeedUser("cl-tc-show");
        var (source, _) = await SeedPlan(
            "Showcase TC", "public", isShowcase: true, stopCount: 1,
            tripContextJson: """{"budget":"mid","groupType":"couple"}""");
        var client = await AppClient(caller, "cl-tc-show-c");

        var res = await client.PostAsync($"/plans/{source.Id}/clone", null);
        var cloneId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var db = fixture.GetDbContext();
        var clone = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == cloneId);
        Assert.NotNull(clone.TripContext);
        Assert.Equal("mid", clone.TripContext!.RootElement.GetProperty("budget").GetString());
    }

    [Fact]
    public async Task Clone_PublicUserPlan_DropsTripContext_ForPrivacy()
    {
        var otherOwner = await SeedUser("cl-tc-owner");
        var caller = await SeedUser("cl-tc-caller");
        var (source, _) = await SeedPlan(
            "Public TC", "public", isShowcase: false, source: "user", ownerId: otherOwner, stopCount: 1,
            tripContextJson: """{"diet":"vegan","budget":"low","exclusions":["nightlife"]}""");
        var client = await AppClient(caller, "cl-tc-caller-c");

        var res = await client.PostAsync($"/plans/{source.Id}/clone", null);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var cloneId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Los datos personales del dueño (dieta/presupuesto/exclusiones) NO se persisten bajo el cloner.
        var db = fixture.GetDbContext();
        var clone = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == cloneId);
        Assert.Null(clone.TripContext);
    }

    // ── MINOR StartDate: NO se hereda la fecha del origen ──
    [Fact]
    public async Task Clone_DoesNotInheritStartDate()
    {
        var caller = await SeedUser("cl-sd");
        var (source, _) = await SeedPlan(
            "Showcase Dated", "public", isShowcase: true, stopCount: 1,
            startDate: new DateOnly(2020, 1, 1));
        var client = await AppClient(caller, "cl-sd-c");

        var res = await client.PostAsync($"/plans/{source.Id}/clone", null);
        var cloneId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var db = fixture.GetDbContext();
        var clone = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == cloneId);
        Assert.Null(clone.StartDate);
    }

    // ── Test gap: bloqueo owner↔caller sobre un plan PÚBLICO → 404 (honra IPlanAccessService) ──
    [Fact]
    public async Task Clone_PublicPlan_OwnerBlockedCaller_Returns404()
    {
        var owner = await SeedUser("cl-blk-owner");
        var caller = await SeedUser("cl-blk-caller");
        var (source, _) = await SeedPlan(
            "Public Blocked", "public", isShowcase: false, source: "user", ownerId: owner, stopCount: 1);

        var db = fixture.GetDbContext();
        db.UserBlocks.Add(new UserBlock { BlockerId = owner, BlockedId = caller });
        await db.SaveChangesAsync();

        var client = await AppClient(caller, "cl-blk-caller-c");
        var res = await client.PostAsync($"/plans/{source.Id}/clone", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        Assert.False(await fixture.GetDbContext().Plans
            .AnyAsync(p => p.CreatedById == caller && p.ClonedFrom == source.Id));
    }

    // ── Test gap: aislamiento BIDIRECCIONAL (editar clon no toca origen; editar origen no toca clon) ──
    [Fact]
    public async Task Clone_Edits_AreIsolated_Bidirectionally()
    {
        var owner = await SeedUser("cl-iso-owner");
        var caller = await SeedUser("cl-iso-caller");
        var (source, srcPlaceIds) = await SeedPlan(
            "Public Iso", "public", isShowcase: false, source: "user", ownerId: owner, stopCount: 2);
        var callerClient = await AppClient(caller, "cl-iso-caller-c");
        var ownerClient = await AppClient(owner, "cl-iso-owner-c");

        var cloneId = (await (await callerClient.PostAsync($"/plans/{source.Id}/clone", null))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // El caller edita SU clon → el origen NO cambia.
        var callerPlace = await SeedPlace("Caller Edit Place");
        var editClone = await callerClient.PutAsJsonAsync($"/plans/{cloneId}/stops", new
        {
            stops = new[] { new { placeId = callerPlace.Id, dayNumber = 1, orderIndex = 0, timeBlock = "morning" } }
        });
        Assert.Equal(HttpStatusCode.OK, editClone.StatusCode);

        var db1 = fixture.GetDbContext();
        var srcStopsAfterCloneEdit = await db1.PlanStops.AsNoTracking()
            .Where(s => s.PlanId == source.Id).Select(s => s.PlaceId).ToListAsync();
        Assert.Equal(srcPlaceIds.OrderBy(x => x), srcStopsAfterCloneEdit.OrderBy(x => x)); // origen intacto

        // El owner edita el ORIGEN → el clon NO cambia (sigue con el place del caller).
        var ownerPlace = await SeedPlace("Owner Edit Place");
        var editSource = await ownerClient.PutAsJsonAsync($"/plans/{source.Id}/stops", new
        {
            stops = new[] { new { placeId = ownerPlace.Id, dayNumber = 1, orderIndex = 0, timeBlock = "morning" } }
        });
        Assert.Equal(HttpStatusCode.OK, editSource.StatusCode);

        var db2 = fixture.GetDbContext();
        var cloneStops = await db2.PlanStops.AsNoTracking()
            .Where(s => s.PlanId == cloneId).Select(s => s.PlaceId).ToListAsync();
        Assert.Equal(new[] { callerPlace.Id }, cloneStops); // clon intacto
    }

    // ── MAJOR (invertido a verde): N clones CONCURRENTES del mismo origen → exactamente 1 plan ──
    [Fact]
    public async Task Clone_ConcurrentSameSource_CreatesExactlyOnePlan()
    {
        var caller = await SeedUser("cl-race");
        var (source, _) = await SeedPlan("Showcase Race", "public", isShowcase: true, stopCount: 2);
        var client = await AppClient(caller, "cl-race-c");

        // 8 clones concurrentes del MISMO origen por el MISMO free user (repro del reviewer).
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => client.PostAsync($"/plans/{source.Id}/clone", null))
            .ToArray();
        var responses = await Task.WhenAll(tasks);

        // Todas 2xx (el ganador 201; los perdedores 200 con el ganador tras 23505).
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"status={(int)r.StatusCode}"));

        // Todas apuntan al MISMO plan.
        var ids = new List<Guid>();
        foreach (var r in responses)
            ids.Add((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Single(ids.Distinct());

        // Y la DB tiene EXACTAMENTE un clon de ese origen para el caller.
        var db = fixture.GetDbContext();
        Assert.Equal(1, await db.Plans.CountAsync(p => p.CreatedById == caller && p.ClonedFrom == source.Id));
    }
}
