using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Features.Admin.Places;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Taxonomy;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Tests de integración de <c>POST /admin/places/bulk</c> (AdminPlacesController).
///
/// Cubre dos gaps detectados en la auditoría:
///   - BulkImport con dedup por GooglePlaceId: 3 entradas, 1 duplicada → created=2, skipped=1.
///   - Sin auth → 401 Unauthorized (el AdminAuthorizationFilter rechaza sin header).
///
/// El admin se simula con un token RS256 tipo Firebase (el mismo que usan
/// <c>AuthTests.Sync_AdminEmail_GetsAdminRole</c>) con un email bajo
/// <c>@locallist.ai</c> — criterio usado por <c>AdminAuthorizationFilter</c>.
/// </summary>
public class AdminPlacesTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task BulkImport_DeduplicatesByGooglePlaceId()
    {
        // Sembramos un place ya existente con un GooglePlaceId concreto para forzar dedup.
        var duplicateGoogleId = $"gpid-dup-{Guid.NewGuid():N}";
        var db = fixture.GetDbContext();
        db.Places.Add(new Place
        {
            Id = Guid.NewGuid(),
            Name = "Already Imported",
            Category = "Food",
            City = "Miami",
            WhyThisPlace = "Sembrado en test",
            Status = "published",
            GooglePlaceId = duplicateGoogleId,
        });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();

        var uniqueId1 = $"gpid-new1-{Guid.NewGuid():N}";
        var uniqueId2 = $"gpid-new2-{Guid.NewGuid():N}";
        var payload = new[]
        {
            new {
                name = $"New Place A {Guid.NewGuid():N}", category = "Food",
                whyThisPlace = "bulk-new-1", city = "Miami",
                googlePlaceId = uniqueId1
            },
            new {
                name = $"New Place B {Guid.NewGuid():N}", category = "Food",
                whyThisPlace = "bulk-new-2", city = "Miami",
                googlePlaceId = uniqueId2
            },
            new {
                // Éste es el duplicado — mismo GooglePlaceId que el sembrado.
                name = "Already Imported (dup)", category = "Food",
                whyThisPlace = "bulk-dup", city = "Miami",
                googlePlaceId = duplicateGoogleId
            },
        };

        var response = await client.PostAsJsonAsync("/admin/places/bulk", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("created").GetInt32());
        Assert.Equal(1, body.GetProperty("skipped").GetInt32());
        Assert.Equal(0, body.GetProperty("errors").GetInt32());
    }

    [Fact]
    public async Task BulkImport_WithoutAuth_Returns401()
    {
        var client = fixture.CreateClient();

        // Sin header Authorization ni X-Admin-Key → AdminAuthorizationFilter rechaza.
        var payload = new[]
        {
            new { name = "Anything", category = "Food", whyThisPlace = "noauth", city = "Miami" }
        };
        var response = await client.PostAsJsonAsync("/admin/places/bulk", payload);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReindexEmbeddings_PopulatesVectorColumn()
    {
        var db = fixture.GetDbContext();
        var seededId = Guid.NewGuid();
        db.Places.Add(new Place
        {
            Id = seededId,
            Name = $"Reindex Target {Guid.NewGuid():N}",
            Category = "Food",
            City = "Miami",
            Neighborhood = "Wynwood",
            WhyThisPlace = "italian bakery with tiki cocktails",
            BestFor = new List<string> { "romantic", "date-night" },
            SuitableFor = new List<string> { "couple" },
            Status = "published",
            GooglePlaceId = $"gpid-reindex-{Guid.NewGuid():N}",
        });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var response = await client.PostAsync("/admin/places/reindex-embeddings", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("reindexed").GetInt32() >= 1);
        Assert.Equal(0, body.GetProperty("failed").GetInt32());

        // Verify the seeded place got a non-null embedding of the right shape
        var freshDb = fixture.GetDbContext();
        var reindexed = await freshDb.Places.FirstAsync(p => p.Id == seededId);
        Assert.NotNull(reindexed.Embedding);
        Assert.Equal(768, reindexed.Embedding!.ToArray().Length);
    }

    [Fact]
    public async Task ReindexEmbeddings_OnlyMissing_SkipsAlreadyIndexed()
    {
        var db = fixture.GetDbContext();
        var untouchedId = Guid.NewGuid();
        var existingVector = new Pgvector.Vector(new float[768]);
        db.Places.Add(new Place
        {
            Id = untouchedId,
            Name = $"Already Indexed {Guid.NewGuid():N}",
            Category = "Coffee",
            City = "Miami",
            WhyThisPlace = "already has embedding",
            Status = "published",
            GooglePlaceId = $"gpid-onlymissing-{Guid.NewGuid():N}",
            Embedding = existingVector,
        });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var response = await client.PostAsync("/admin/places/reindex-embeddings?onlyMissing=true", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // El place existente no debe haber sido reindexado (vector sin cambios — todo ceros)
        var freshDb = fixture.GetDbContext();
        var untouched = await freshDb.Places.FirstAsync(p => p.Id == untouchedId);
        var arr = untouched.Embedding!.ToArray();
        Assert.Equal(768, arr.Length);
        Assert.All(arr, v => Assert.Equal(0f, v));
    }

    [Fact]
    public async Task ReindexEmbeddings_WithoutAuth_Returns401()
    {
        var client = fixture.CreateClient();
        var response = await client.PostAsync("/admin/places/reindex-embeddings", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DbRoleAdmin_WithHs256_GetsForbiddenOnAdminEndpoint()
    {
        // The Role column in DB is decorative for authorization — AdminAuthorizationFilter
        // checks JWT issuer (Firebase RS256) + email domain, not the DB Role value.
        // A user with Role="admin" in DB but an HS256 token must still get 403.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var email = $"role-admin-{tag}@test.com";
        var client = fixture.CreateClient();

        // Register via app flow to obtain a real HS256 token
        var registerBody = await (await client.PostAsJsonAsync("/auth/register",
            new { email, password = "TestPass1!" })).Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = registerBody.GetProperty("accessToken").GetString()!;

        // Elevate Role in DB directly (simulates someone granted "admin" in DB without
        // a Firebase account — must NOT be sufficient to access admin endpoints)
        var db = fixture.GetDbContext();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        user.Role = "admin";
        await db.SaveChangesAsync();

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync("/admin/places");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TranslateBatch_LimitSmallerThanPending_Returns_RemainingGreaterThanZero()
    {
        var db = fixture.GetDbContext();
        for (var i = 0; i < 3; i++)
        {
            db.Places.Add(new Place
            {
                Id = Guid.NewGuid(),
                Name = $"Translate Target {Guid.NewGuid():N}",
                Category = "Food",
                City = "Miami",
                WhyThisPlace = "test",
                Status = "published",
                Source = "curated",
            });
        }
        await db.SaveChangesAsync();

        // Return a valid place translation for each Gemini call
        fixture.FakeGemini.Responder = _ => GeminiOk("""{"name":"Nombre ES","whyThisPlace":"Por qué","bestTime":"Tarde","neighborhood":"Barrio","subcategory":"Restaurante","bestFor":["todos"],"suitableFor":["familias"]}""");
        try
        {
            var client = CreateAdminClient();
            var response = await client.PostAsync("/admin/places/translate-batch?lang=es&limit=1", content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, body.GetProperty("translated").GetInt32());
            Assert.True(body.GetProperty("remaining").GetInt32() > 0);
        }
        finally
        {
            fixture.FakeGemini.Responder = null;
        }
    }

    [Fact]
    public async Task TranslateBatch_AllFitWithinLimit_Returns_RemainingZero()
    {
        var db = fixture.GetDbContext();
        var uniquePrefix = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 2; i++)
        {
            db.Places.Add(new Place
            {
                Id = Guid.NewGuid(),
                Name = $"AllFit {uniquePrefix} {i}",
                Category = "Food",
                City = "Miami",
                WhyThisPlace = "test",
                Status = "published",
                Source = "curated",
            });
        }
        await db.SaveChangesAsync();

        fixture.FakeGemini.Responder = _ => GeminiOk("""{"name":"Nombre ES","whyThisPlace":"Por qué","bestTime":"Tarde","neighborhood":"Barrio","subcategory":"Restaurante","bestFor":["todos"],"suitableFor":["familias"]}""");
        try
        {
            var client = CreateAdminClient();
            // limit=10 (default) > 2 seeded — all should be translated
            var response = await client.PostAsync("/admin/places/translate-batch?lang=es", content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(0, body.GetProperty("remaining").GetInt32());
        }
        finally
        {
            fixture.FakeGemini.Responder = null;
        }
    }

    // ── import-from-urls ───────────────────────────────────────────────────

    [Fact]
    public async Task ImportFromUrls_EmptyList_Returns400()
    {
        var client = CreateAdminClient();
        var response = await client.PostAsJsonAsync("/admin/places/import-from-urls",
            new { urls = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ImportFromUrls_Over500_Returns400()
    {
        var client = CreateAdminClient();
        var urls = Enumerable.Range(0, 501).Select(i => $"https://maps.app.goo.gl/x{i}").ToArray();
        var response = await client.PostAsJsonAsync("/admin/places/import-from-urls",
            new { urls });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ImportFromUrls_ShortLink_ReturnsFailedResolveWithHelpMessage()
    {
        var client = CreateAdminClient();
        var response = await client.PostAsJsonAsync("/admin/places/import-from-urls",
            new { urls = new[] { "https://maps.app.goo.gl/2ZXNjvJRnFKKA39v7" }, defaultCity = "Miami" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("created").GetInt32());
        Assert.Equal(1, body.GetProperty("failed").GetInt32());
        var row = body.GetProperty("rows")[0];
        Assert.Equal("failed_resolve", row.GetProperty("status").GetString());
        // Message should guide the user — not the generic "Could not extract a Place ID"
        var msg = row.GetProperty("error").GetString() ?? "";
        Assert.Contains("navegador", msg);
    }

    [Fact]
    public async Task ImportFromUrls_UnresolvableUrl_ReturnsFailedResolveRow()
    {
        fixture.FakeGooglePlaces.Reset();
        // Resolve returns null → failed_resolve
        var client = CreateAdminClient();
        var response = await client.PostAsJsonAsync("/admin/places/import-from-urls",
            new { urls = new[] { "https://not-google.com/foo" }, defaultCity = "Miami" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("created").GetInt32());
        Assert.Equal(1, body.GetProperty("failed").GetInt32());
        var row = body.GetProperty("rows")[0];
        Assert.Equal("failed_resolve", row.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ImportFromUrls_PlaceIdResolvedButDetailsFail_ReturnsFailedDetailsRow()
    {
        fixture.FakeGooglePlaces.Reset();
        var resolvedId = $"ChIJ-test-{Guid.NewGuid():N}";
        fixture.FakeGooglePlaces.ResolvedByUrl["https://www.google.com/maps/place/testplace-abc/"] = resolvedId;
        // DetailsByPlaceId has no entry → GetDetailsAsync returns null

        var client = CreateAdminClient();
        var response = await client.PostAsJsonAsync("/admin/places/import-from-urls",
            new { urls = new[] { "https://www.google.com/maps/place/testplace-abc/" }, defaultCity = "Miami" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("created").GetInt32());
        Assert.Equal(0, body.GetProperty("skipped").GetInt32());
        Assert.Equal(1, body.GetProperty("failed").GetInt32());
        var row = body.GetProperty("rows")[0];
        Assert.Equal("failed_details", row.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ImportFromUrls_HappyPath_CreatesPlaceWithEmbedding()
    {
        fixture.FakeGooglePlaces.Reset();
        var placeId = $"ChIJhappy-{Guid.NewGuid():N}";
        var placeName = $"Test Place {Guid.NewGuid():N}";

        fixture.FakeGooglePlaces.ResolvedByUrl["https://www.google.com/maps/place/testplace-happy/"] = placeId;
        fixture.FakeGooglePlaces.DetailsByPlaceId[placeId] = new LocalList.API.NET.Features.Admin.Places.GooglePlaceDetails(
            Id: placeId, Name: placeName, FormattedAddress: "123 Test St, Miami, FL",
            City: "Miami", Neighborhood: "Wynwood",
            Lat: 25.77m, Lng: -80.19m,
            PrimaryType: "italian_restaurant", Types: ["italian_restaurant", "restaurant"],
            PriceLevel: "$",
            Photos: ["https://photos.example.com/photo1.jpg"],
            Rating: 4.5m, ReviewCount: 200,
            Website: null, Phone: null,
            EditorialSummary: "A great test place");

        var client = CreateAdminClient();
        var response = await client.PostAsJsonAsync("/admin/places/import-from-urls",
            new { urls = new[] { "https://www.google.com/maps/place/testplace-happy/" }, defaultCity = "Miami" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("created").GetInt32());
        Assert.Equal(0, body.GetProperty("skipped").GetInt32());
        Assert.Equal(0, body.GetProperty("failed").GetInt32());
        Assert.Equal("created", body.GetProperty("rows")[0].GetProperty("status").GetString());

        // Verify full taxonomy × price × rating × photo pipeline in DB
        var freshDb = fixture.GetDbContext();
        var saved = await freshDb.Places.FirstOrDefaultAsync(p => p.GooglePlaceId == placeId);
        Assert.NotNull(saved);
        Assert.Equal(placeName, saved!.Name);
        Assert.Equal("Food", saved.Category);
        Assert.Contains("Italian", saved.Subcategories ?? []);
        Assert.Equal("$", saved.PriceRange);
        Assert.Equal(4.5m, saved.GoogleRating);
        Assert.NotNull(saved.Photos);
        Assert.Equal(1, saved.Photos!.Count);
        Assert.Equal(25.77m, saved.Latitude);
        Assert.Equal(-80.19m, saved.Longitude);
        Assert.NotNull(saved.Embedding);
    }

    [Fact]
    public async Task ImportFromUrls_DuplicatePlaceId_Skipped()
    {
        fixture.FakeGooglePlaces.Reset();
        var placeId = $"ChIJdup-{Guid.NewGuid():N}";
        var db = fixture.GetDbContext();
        db.Places.Add(new Place
        {
            Id = Guid.NewGuid(), Name = "Already Exists", Category = "Food",
            City = "Miami", WhyThisPlace = "existing", Status = "published",
            GooglePlaceId = placeId
        });
        await db.SaveChangesAsync();

        fixture.FakeGooglePlaces.ResolvedByUrl["https://www.google.com/maps/place/testplace-dup/"] = placeId;
        fixture.FakeGooglePlaces.DetailsByPlaceId[placeId] = new LocalList.API.NET.Features.Admin.Places.GooglePlaceDetails(
            placeId, "Already Exists", null, "Miami", null,
            25.77m, -80.19m, "restaurant", ["restaurant"],
            null, [], null, null, null, null, null);

        var client = CreateAdminClient();
        var response = await client.PostAsJsonAsync("/admin/places/import-from-urls",
            new { urls = new[] { "https://www.google.com/maps/place/testplace-dup/" }, defaultCity = "Miami" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("created").GetInt32());
        Assert.Equal(1, body.GetProperty("skipped").GetInt32());
        Assert.Equal("skipped_duplicate", body.GetProperty("rows")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task ImportFromUrls_EmbeddingProviderThrows_ReturnsCreatedWithoutEmbedding()
    {
        fixture.FakeGooglePlaces.Reset();
        var placeId = $"ChIJemb-{Guid.NewGuid():N}";
        var placeName = $"Embed Fail Place {Guid.NewGuid():N}";

        fixture.FakeGooglePlaces.ResolvedByUrl["https://www.google.com/maps/place/testplace-emb/"] = placeId;
        fixture.FakeGooglePlaces.DetailsByPlaceId[placeId] = new LocalList.API.NET.Features.Admin.Places.GooglePlaceDetails(
            Id: placeId, Name: placeName, FormattedAddress: null,
            City: "Miami", Neighborhood: null,
            Lat: 25.77m, Lng: -80.19m,
            PrimaryType: "restaurant", Types: ["restaurant"],
            PriceLevel: null, Photos: [],
            Rating: null, ReviewCount: null,
            Website: null, Phone: null, EditorialSummary: null);

        fixture.FakeEmbeddings.Responder = _ => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway);
        try
        {
            var client = CreateAdminClient();
            var response = await client.PostAsJsonAsync("/admin/places/import-from-urls",
                new { urls = new[] { "https://www.google.com/maps/place/testplace-emb/" }, defaultCity = "Miami" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, body.GetProperty("created").GetInt32());
            Assert.Equal(0, body.GetProperty("failed").GetInt32());

            // Place is created but embedding is null (resilience: embedding failure must not block insert)
            var freshDb = fixture.GetDbContext();
            var saved = await freshDb.Places.FirstOrDefaultAsync(p => p.GooglePlaceId == placeId);
            Assert.NotNull(saved);
            Assert.Null(saved!.Embedding);
        }
        finally
        {
            fixture.FakeEmbeddings.Responder = null;
        }
    }

    // ── PostponePlace ──────────────────────────────────────────────────────

    [Fact]
    public async Task PostponePlace_SetsReviewDeferredAt_AndPlacesItFirstInInReviewList()
    {
        var db = fixture.GetDbContext();
        var tag = Guid.NewGuid().ToString("N")[..8];

        // Seed 3 in_review places with staggered CreatedAt so ordering is deterministic
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-30);
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid();

        db.Places.AddRange(
            new Place { Id = idA, Name = $"Place A {tag}", Category = "Food", City = "Miami", WhyThisPlace = "a", Status = "in_review", CreatedAt = baseTime },
            new Place { Id = idB, Name = $"Place B {tag}", Category = "Food", City = "Miami", WhyThisPlace = "b", Status = "in_review", CreatedAt = baseTime.AddMinutes(5) },
            new Place { Id = idC, Name = $"Place C {tag}", Category = "Food", City = "Miami", WhyThisPlace = "c", Status = "in_review", CreatedAt = baseTime.AddMinutes(10) }
        );
        await db.SaveChangesAsync();

        var client = CreateAdminClient();

        // Postpone place B
        var postponeRes = await client.PatchAsync($"/admin/places/{idB}/postpone", content: null);
        Assert.Equal(HttpStatusCode.OK, postponeRes.StatusCode);

        // Verify DB: review_deferred_at is set
        var freshDb = fixture.GetDbContext();
        var saved = await freshDb.Places.FirstAsync(p => p.Id == idB);
        Assert.NotNull(saved.ReviewDeferredAt);

        // GET in_review: postponed place must appear first (index 0)
        var listRes = await client.GetAsync("/admin/places?status=in_review");
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        var body = await listRes.Content.ReadFromJsonAsync<JsonElement>();
        var placesArr = body.GetProperty("places");
        Assert.True(placesArr.GetArrayLength() >= 3);
        Assert.Equal(idB.ToString(), placesArr[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task PostponePlace_NotFound_Returns404()
    {
        var client = CreateAdminClient();
        var response = await client.PatchAsync($"/admin/places/{Guid.NewGuid()}/postpone", content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── PR-B: Gemini description generation ──────────────────────────────

    [Fact]
    public async Task BulkImport_WithEmptyDescription_CallsGeminiAndPersistsResult()
    {
        fixture.FakeGemini.Responder = _ => GeminiOk("A lively spot beloved for its craft cocktails and warm ambiance.");
        try
        {
            var client = CreateAdminClient();
            var payload = new[]
            {
                new { name = $"Empty Desc Place {Guid.NewGuid():N}", category = "Nightlife",
                      whyThisPlace = (string?)null, city = "Miami" }
            };
            var response = await client.PostAsJsonAsync("/admin/places/bulk", payload);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, body.GetProperty("created").GetInt32());

            // Verify description was generated and persisted (not empty)
            var db = fixture.GetDbContext();
            var placeName = payload[0].name;
            var place = await db.Places.FirstAsync(p => p.Name == placeName);
            Assert.False(string.IsNullOrEmpty(place.WhyThisPlace));
            Assert.DoesNotContain("Pending curatorial", place.WhyThisPlace);
        }
        finally { fixture.FakeGemini.Responder = null; }
    }

    [Fact]
    public async Task BulkImport_WithManualDescription_DoesNotCallGemini()
    {
        var callCount = 0;
        fixture.FakeGemini.Responder = req =>
        {
            // Only count calls that look like a description generation (not other Gemini calls)
            callCount++;
            return GeminiOk("should not be called");
        };
        try
        {
            var client = CreateAdminClient();
            var manualDesc = "Handcrafted description — already written by curator.";
            var payload = new[]
            {
                new { name = $"Manual Desc Place {Guid.NewGuid():N}", category = "Food",
                      whyThisPlace = manualDesc, city = "Miami" }
            };
            var countBefore = callCount;
            var response = await client.PostAsJsonAsync("/admin/places/bulk", payload);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(countBefore, callCount); // no Gemini calls triggered

            var db = fixture.GetDbContext();
            var placeName = payload[0].name;
            var place = await db.Places.FirstAsync(p => p.Name == placeName);
            Assert.Equal(manualDesc, place.WhyThisPlace);
        }
        finally { fixture.FakeGemini.Responder = null; }
    }

    [Fact]
    public async Task BulkImport_WithGooglePlaceholder_CallsGeminiAndReplaces()
    {
        const string placeholder = "Imported from Google Places. Pending curatorial copy.";
        fixture.FakeGemini.Responder = _ => GeminiOk("The city's best-kept secret for handmade pasta.");
        try
        {
            var client = CreateAdminClient();
            var payload = new[]
            {
                new { name = $"Placeholder Desc {Guid.NewGuid():N}", category = "Food",
                      whyThisPlace = placeholder, city = "Miami" }
            };
            var response = await client.PostAsJsonAsync("/admin/places/bulk", payload);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var db = fixture.GetDbContext();
            var placeName = payload[0].name;
            var place = await db.Places.FirstAsync(p => p.Name == placeName);
            Assert.NotEqual(placeholder, place.WhyThisPlace);
            Assert.False(string.IsNullOrEmpty(place.WhyThisPlace));
        }
        finally { fixture.FakeGemini.Responder = null; }
    }

    [Fact]
    public async Task SuggestDescription_WithoutAuth_Returns401()
    {
        var client = fixture.CreateClient();
        var response = await client.PostAsync($"/admin/places/{Guid.NewGuid()}/suggest-description", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SuggestDescription_ReturnsGeneratedText_DoesNotPersist()
    {
        var db = fixture.GetDbContext();
        var original = "Original description — should not change.";
        var seededId = Guid.NewGuid();
        db.Places.Add(new Place
        {
            Id = seededId,
            Name = $"Suggest Target {Guid.NewGuid():N}",
            Category = "Coffee",
            City = "Miami",
            WhyThisPlace = original,
            Status = "in_review",
        });
        await db.SaveChangesAsync();

        fixture.FakeGemini.Responder = _ => GeminiOk("Sun-drenched terrace with specialty single-origin brews.");
        try
        {
            var client = CreateAdminClient();
            var response = await client.PostAsync($"/admin/places/{seededId}/suggest-description", content: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var suggested = body.GetProperty("whyThisPlace").GetString();
            Assert.False(string.IsNullOrEmpty(suggested));

            // Must NOT have persisted — DB still has the original
            var freshDb = fixture.GetDbContext();
            var unchanged = await freshDb.Places.FirstAsync(p => p.Id == seededId);
            Assert.Equal(original, unchanged.WhyThisPlace);
        }
        finally { fixture.FakeGemini.Responder = null; }
    }

    [Fact]
    public async Task SuggestDescription_GeminiUnavailable_Returns503()
    {
        var db = fixture.GetDbContext();
        var seededId = Guid.NewGuid();
        db.Places.Add(new Place
        {
            Id = seededId,
            Name = $"Suggest 503 {Guid.NewGuid():N}",
            Category = "Shopping",
            City = "Miami",
            WhyThisPlace = "placeholder",
            Status = "in_review",
        });
        await db.SaveChangesAsync();

        fixture.FakeGemini.Responder = _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
        try
        {
            var client = CreateAdminClient();
            var response = await client.PostAsync($"/admin/places/{seededId}/suggest-description", content: null);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally { fixture.FakeGemini.Responder = null; }
    }

    [Fact]
    public async Task BackfillDescriptions_DryRun_DoesNotMutate()
    {
        var db = fixture.GetDbContext();
        var seededId = Guid.NewGuid();
        db.Places.Add(new Place
        {
            Id = seededId,
            Name = $"Backfill DryRun {Guid.NewGuid():N}",
            Category = "Wellness",
            City = "Miami",
            WhyThisPlace = "",
            Status = "in_review",
        });
        await db.SaveChangesAsync();

        fixture.FakeGemini.Responder = _ => GeminiOk("Should not appear.");
        try
        {
            var client = CreateAdminClient();
            var response = await client.PostAsync("/admin/places/backfill-descriptions?dryRun=true&limit=10", content: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("dryRun").GetBoolean());
            Assert.True(body.GetProperty("candidates").GetInt32() >= 1);

            // DB must be unchanged
            var freshDb = fixture.GetDbContext();
            var place = await freshDb.Places.FirstAsync(p => p.Id == seededId);
            Assert.Equal("", place.WhyThisPlace);
        }
        finally { fixture.FakeGemini.Responder = null; }
    }

    [Fact]
    public async Task BackfillDescriptions_OnlyTouchesPlaceholderOrEmpty()
    {
        var db = fixture.GetDbContext();
        var realDescId = Guid.NewGuid();
        var emptyId = Guid.NewGuid();
        var placeholderId = Guid.NewGuid();

        db.Places.AddRange(
            new Place { Id = realDescId, Name = $"Real Desc {Guid.NewGuid():N}", Category = "Food",
                City = "Miami", WhyThisPlace = "A real handcrafted description.", Status = "in_review" },
            new Place { Id = emptyId, Name = $"Empty Desc {Guid.NewGuid():N}", Category = "Food",
                City = "Miami", WhyThisPlace = "", Status = "in_review" },
            new Place { Id = placeholderId, Name = $"Placeholder Desc {Guid.NewGuid():N}", Category = "Food",
                City = "Miami", WhyThisPlace = "Imported from Google Places. Pending curatorial copy.",
                Status = "in_review" }
        );
        await db.SaveChangesAsync();

        fixture.FakeGemini.Responder = _ => GeminiOk("Fresh AI description for the place.");
        try
        {
            var client = CreateAdminClient();
            var response = await client.PostAsync("/admin/places/backfill-descriptions?dryRun=false&limit=200", content: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var freshDb = fixture.GetDbContext();
            var realDesc = await freshDb.Places.FirstAsync(p => p.Id == realDescId);
            var empty = await freshDb.Places.FirstAsync(p => p.Id == emptyId);
            var placeholder = await freshDb.Places.FirstAsync(p => p.Id == placeholderId);

            Assert.Equal("A real handcrafted description.", realDesc.WhyThisPlace);
            Assert.False(string.IsNullOrEmpty(empty.WhyThisPlace));
            Assert.NotEqual("Imported from Google Places. Pending curatorial copy.", placeholder.WhyThisPlace);
        }
        finally { fixture.FakeGemini.Responder = null; }
    }

    [Fact]
    public async Task GetPlaces_WithSearch_FiltersByNameCaseInsensitive()
    {
        var db = fixture.GetDbContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        db.Places.AddRange(
            new Place { Id = Guid.NewGuid(), Name = $"Ramen Yokohama {suffix}", Category = "Food", City = "Miami", WhyThisPlace = "test", Status = "in_review" },
            new Place { Id = Guid.NewGuid(), Name = $"Sushi Bar {suffix}", Category = "Food", City = "Miami", WhyThisPlace = "test", Status = "in_review" },
            new Place { Id = Guid.NewGuid(), Name = $"Burger House {suffix}", Category = "Food", City = "Miami", WhyThisPlace = "test", Status = "in_review" }
        );
        await db.SaveChangesAsync();

        var client = CreateAdminClient();

        var resLower = await client.GetAsync($"/admin/places?status=in_review&search=ramen+yokohama+{suffix}");
        Assert.Equal(HttpStatusCode.OK, resLower.StatusCode);
        var bodyLower = await resLower.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, bodyLower.GetProperty("total").GetInt32());

        var resUpper = await client.GetAsync($"/admin/places?status=in_review&search=RAMEN+YOKOHAMA+{suffix}");
        Assert.Equal(HttpStatusCode.OK, resUpper.StatusCode);
        var bodyUpper = await resUpper.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, bodyUpper.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetPlaces_WithSearch_AppliesAfterStatusFilter()
    {
        var db = fixture.GetDbContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        db.Places.AddRange(
            new Place { Id = Guid.NewGuid(), Name = $"Ramen Search {suffix}", Category = "Food", City = "Miami", WhyThisPlace = "test", Status = "in_review" },
            new Place { Id = Guid.NewGuid(), Name = $"Ramen Search {suffix}", Category = "Food", City = "Miami", WhyThisPlace = "test", Status = "published" }
        );
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var res = await client.GetAsync($"/admin/places?status=in_review&search=ramen+search+{suffix}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal("in_review", body.GetProperty("places")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetPlaces_SearchWithLikeWildcards_TreatsAsLiteral()
    {
        var db = fixture.GetDbContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        db.Places.AddRange(
            new Place { Id = Guid.NewGuid(), Name = $"100% Pure Ramen {suffix}", Category = "Food", City = "Miami", WhyThisPlace = "test", Status = "in_review" },
            new Place { Id = Guid.NewGuid(), Name = $"Pure Ramen {suffix}", Category = "Food", City = "Miami", WhyThisPlace = "test", Status = "in_review" },
            new Place { Id = Guid.NewGuid(), Name = $"Pure_Bar {suffix}", Category = "Food", City = "Miami", WhyThisPlace = "test", Status = "in_review" },
            new Place { Id = Guid.NewGuid(), Name = $"Pure Bar {suffix}", Category = "Food", City = "Miami", WhyThisPlace = "test", Status = "in_review" }
        );
        await db.SaveChangesAsync();

        var client = CreateAdminClient();

        // "%" must match only the name literally containing "%" — not all rows.
        var resPercent = await client.GetAsync($"/admin/places?status=in_review&search=100%25+Pure+Ramen+{suffix}");
        Assert.Equal(HttpStatusCode.OK, resPercent.StatusCode);
        var bodyPercent = await resPercent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, bodyPercent.GetProperty("total").GetInt32());

        // "_" must match only the name literally containing "_" — not any 1-char wildcard.
        var resUnderscore = await client.GetAsync($"/admin/places?status=in_review&search=Pure_Bar+{suffix}");
        Assert.Equal(HttpStatusCode.OK, resUnderscore.StatusCode);
        var bodyUnderscore = await resUnderscore.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, bodyUnderscore.GetProperty("total").GetInt32());

        // Backslash must not cause a 500.
        var resBackslash = await client.GetAsync($"/admin/places?status=in_review&search={Uri.EscapeDataString(@"\")}");
        Assert.Equal(HttpStatusCode.OK, resBackslash.StatusCode);
    }

    [Fact]
    public async Task GetPlaces_SearchOverlong_TruncatesAt100()
    {
        var db = fixture.GetDbContext();
        db.Places.Add(new Place { Id = Guid.NewGuid(), Name = "Ramen Yokohama", Category = "Food", City = "Miami", WhyThisPlace = "test", Status = "in_review" });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        // 200-char search — server truncates, never 500.
        var longSearch = new string('a', 200);
        var res = await client.GetAsync($"/admin/places?status=in_review&search={longSearch}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ── BackfillDescriptions: Google-first + Gemini fallback ──────────────

    [Fact]
    public async Task BackfillDescriptions_GoogleFirst_UsesEditorialWhenAvailable()
    {
        var googlePlaceId = $"ChIJbackfill-google-{Guid.NewGuid():N}";
        const string editorial = "A sun-drenched terrace beloved by locals for craft ramen.";

        var db = fixture.GetDbContext();
        var placeId = Guid.NewGuid();
        db.Places.Add(new Place
        {
            Id = placeId,
            Name = $"Google Editorial Test {Guid.NewGuid():N}",
            Category = "Food",
            City = "Miami",
            WhyThisPlace = PlaceTaxonomy.GooglePlaceholderWhyThisPlace,
            Status = "in_review",
            GooglePlaceId = googlePlaceId,
        });
        await db.SaveChangesAsync();

        fixture.FakeGooglePlaces.DetailsByPlaceId[googlePlaceId] = new GooglePlaceDetails(
            Id: googlePlaceId, Name: "Test", FormattedAddress: null,
            City: "Miami", Neighborhood: null,
            Lat: 25.77m, Lng: -80.19m,
            PrimaryType: "restaurant", Types: ["restaurant"],
            PriceLevel: null, Photos: [],
            Rating: null, ReviewCount: null,
            Website: null, Phone: null,
            EditorialSummary: editorial);

        fixture.FakeGemini.Responder = _ => GeminiOk("should not be used");
        try
        {
            var client = CreateAdminClient();
            var response = await client.PostAsync(
                "/admin/places/backfill-descriptions?dryRun=false&limit=10", content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("googleFilled").GetInt32() >= 1);

            var freshDb = fixture.GetDbContext();
            var saved = await freshDb.Places.FirstAsync(p => p.Id == placeId);
            // Our specific place must be filled from Google, not Gemini
            Assert.Equal(editorial, saved.WhyThisPlace);
        }
        finally
        {
            fixture.FakeGemini.Responder = null;
            fixture.FakeGooglePlaces.DetailsByPlaceId.Remove(googlePlaceId);
        }
    }

    [Fact]
    public async Task BackfillDescriptions_FallsBackToGemini_WhenGoogleHasNoEditorial()
    {
        var googlePlaceId = $"ChIJbackfill-noedit-{Guid.NewGuid():N}";
        const string geminiText = "A LocalList-curated space where community gathers.";

        var db = fixture.GetDbContext();
        var placeId = Guid.NewGuid();
        db.Places.Add(new Place
        {
            Id = placeId,
            Name = $"Google No Editorial {Guid.NewGuid():N}",
            Category = "Coffee",
            City = "Miami",
            WhyThisPlace = PlaceTaxonomy.GooglePlaceholderWhyThisPlace,
            Status = "in_review",
            GooglePlaceId = googlePlaceId,
        });
        await db.SaveChangesAsync();

        fixture.FakeGooglePlaces.DetailsByPlaceId[googlePlaceId] = new GooglePlaceDetails(
            Id: googlePlaceId, Name: "Test", FormattedAddress: null,
            City: "Miami", Neighborhood: null,
            Lat: 25.77m, Lng: -80.19m,
            PrimaryType: "coffee_shop", Types: ["coffee_shop"],
            PriceLevel: null, Photos: [],
            Rating: null, ReviewCount: null,
            Website: null, Phone: null,
            EditorialSummary: null);

        fixture.FakeGemini.Responder = _ => GeminiOk(geminiText);
        try
        {
            var client = CreateAdminClient();
            var response = await client.PostAsync(
                "/admin/places/backfill-descriptions?dryRun=false&limit=10", content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            // Aggregate counts may include other fixture-seeded places; verify the specific place
            Assert.True(body.GetProperty("geminiFilled").GetInt32() >= 1);

            var freshDb = fixture.GetDbContext();
            var saved = await freshDb.Places.FirstAsync(p => p.Id == placeId);
            Assert.Equal(geminiText, saved.WhyThisPlace);
        }
        finally
        {
            fixture.FakeGemini.Responder = null;
            fixture.FakeGooglePlaces.DetailsByPlaceId.Remove(googlePlaceId);
        }
    }

    [Fact]
    public async Task BackfillDescriptions_ExcludesRejectedPlaces()
    {
        var db = fixture.GetDbContext();
        var rejectedId = Guid.NewGuid();
        db.Places.Add(new Place
        {
            Id = rejectedId,
            Name = $"Rejected Placeholder {Guid.NewGuid():N}",
            Category = "Food",
            City = "Miami",
            WhyThisPlace = PlaceTaxonomy.GooglePlaceholderWhyThisPlace,
            Status = "rejected",
        });
        await db.SaveChangesAsync();

        fixture.FakeGemini.Responder = _ => GeminiOk("should not be used for rejected");
        try
        {
            var client = CreateAdminClient();
            var response = await client.PostAsync(
                "/admin/places/backfill-descriptions?dryRun=false&limit=200", content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var freshDb = fixture.GetDbContext();
            var rejected = await freshDb.Places.FirstAsync(p => p.Id == rejectedId);
            // Placeholder cleared by legacy sweep, but no Gemini description generated
            Assert.NotEqual("should not be used for rejected", rejected.WhyThisPlace);
        }
        finally { fixture.FakeGemini.Responder = null; }
    }

    [Fact]
    public async Task BackfillDescriptions_ClearsLegacyPlaceholderAtStart()
    {
        var db = fixture.GetDbContext();
        var placeId = Guid.NewGuid();
        db.Places.Add(new Place
        {
            Id = placeId,
            Name = $"Legacy Placeholder {Guid.NewGuid():N}",
            Category = "Food",
            City = "Miami",
            WhyThisPlace = PlaceTaxonomy.GooglePlaceholderWhyThisPlace,
            Status = "in_review",
        });
        await db.SaveChangesAsync();

        // Gemini returns 429 to simulate rate limit; placeholder should still be cleared
        fixture.FakeGemini.Responder = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        try
        {
            var client = CreateAdminClient();
            var response = await client.PostAsync(
                "/admin/places/backfill-descriptions?dryRun=false&limit=10", content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("clearedLegacyPlaceholder").GetInt32() >= 1);

            var freshDb = fixture.GetDbContext();
            var saved = await freshDb.Places.FirstAsync(p => p.Id == placeId);
            Assert.Equal("", saved.WhyThisPlace);
        }
        finally
        {
            fixture.FakeGemini.Responder = null;
            // Remove the empty place so it doesn't bleed into other backfill tests
            var cleanupDb = fixture.GetDbContext();
            var leftover = await cleanupDb.Places.FindAsync(placeId);
            if (leftover != null) { cleanupDb.Places.Remove(leftover); await cleanupDb.SaveChangesAsync(); }
        }
    }

    [Fact]
    public async Task BackfillDescriptions_DryRun_DoesNotClearLegacyPlaceholder()
    {
        var db = fixture.GetDbContext();
        var placeId = Guid.NewGuid();
        db.Places.Add(new Place
        {
            Id = placeId,
            Name = $"DryRun Placeholder Guard {Guid.NewGuid():N}",
            Category = "Culture",
            City = "Miami",
            WhyThisPlace = PlaceTaxonomy.GooglePlaceholderWhyThisPlace,
            Status = "in_review",
        });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var response = await client.PostAsync(
            "/admin/places/backfill-descriptions?dryRun=true&limit=10", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("dryRun").GetBoolean());

        // DB must be untouched when dryRun=true
        var freshDb = fixture.GetDbContext();
        var saved = await freshDb.Places.FirstAsync(p => p.Id == placeId);
        Assert.Equal(PlaceTaxonomy.GooglePlaceholderWhyThisPlace, saved.WhyThisPlace);
    }

    [Fact]
    public async Task GoogleSearch_IncludesEditorialSummary_WhenGoogleReturnsIt()
    {
        const string editorial = "A cozy ramen joint with handmade noodles.";
        fixture.FakeGooglePlaces.SearchResponder = (_, _) => Task.FromResult<List<GooglePlacePreview>?>(
        [
            new GooglePlacePreview(
                GooglePlaceId: $"ChIJtest-{Guid.NewGuid():N}",
                Name: "Test Ramen",
                FormattedAddress: "123 Main St, Miami",
                Lat: 25.76m, Lng: -80.19m,
                Rating: 4.5m, ReviewCount: 200,
                PriceLevel: "$$",
                Photos: [],
                Types: ["restaurant"],
                Website: null,
                Phone: null,
                EditorialSummary: editorial,
                ExistsInLib: false)
        ]);

        try
        {
            var client = CreateAdminClient();
            var response = await client.PostAsJsonAsync("/admin/places/google-search",
                new { query = "ramen", city = "Miami" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var first = body.GetProperty("results")[0];
            Assert.Equal(editorial, first.GetProperty("editorialSummary").GetString());
        }
        finally { fixture.FakeGooglePlaces.SearchResponder = null; }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static HttpResponseMessage GeminiOk(string text)
    {
        var envelope = $"{{\"candidates\":[{{\"content\":{{\"parts\":[{{\"text\":{System.Text.Json.JsonSerializer.Serialize(text)}}}]}}}}]}}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private HttpClient CreateAdminClient()
    {
        var adminEmail = $"admin-{Guid.NewGuid():N}@locallist.ai";
        var adminFbUid = $"fb-admin-{Guid.NewGuid():N}";

        // Pre-sembramos el usuario admin para que GetUserIdAsync dentro del controller
        // pueda resolver el SubmittedById con el flujo Firebase legado.
        var db = fixture.GetDbContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Email = adminEmail,
            FirebaseUid = adminFbUid,
            Role = "admin"
        });
        db.SaveChanges();

        var client = fixture.CreateClient();
        var token = fixture.CreateToken(adminFbUid, adminEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
