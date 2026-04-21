using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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

    // ── Helpers ────────────────────────────────────────────────────────────

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
