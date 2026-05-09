using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Data.Entities;

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
