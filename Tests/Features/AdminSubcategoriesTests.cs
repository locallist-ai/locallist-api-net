using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

public class AdminSubcategoriesTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Create_WithoutAuth_Returns401()
    {
        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/admin/subcategories",
            new { categoryKey = "Food", key = "test-unauth", labelEn = "Test", labelEs = "Test" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_Valid_PersistsAndAppearsInTaxonomy()
    {
        var client = CreateAdminClient();
        var key = $"test-{Guid.NewGuid():N}".Substring(0, 16).ToLower().Replace("_", "-");

        var response = await client.PostAsJsonAsync("/admin/subcategories",
            new { categoryKey = "Coffee", key, labelEn = "Test Coffee Sub", labelEs = "Sub Café Test" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Coffee", body.GetProperty("categoryKey").GetString());
        Assert.Equal(key, body.GetProperty("key").GetString());
        Assert.Equal("Test Coffee Sub", body.GetProperty("labelEn").GetString());

        // Verify it appears in GET /taxonomy (new client = new cache scope)
        var anonClient = fixture.CreateClient();
        var tax = await anonClient.GetAsync("/taxonomy");
        var taxBody = await tax.Content.ReadFromJsonAsync<JsonElement>();
        var coffeeSubs = taxBody.GetProperty("subcategoriesByCategory").GetProperty("Coffee")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(key, coffeeSubs);
    }

    [Fact]
    public async Task Create_DuplicateKey_Returns409()
    {
        var client = CreateAdminClient();
        var key = $"dup-{Guid.NewGuid():N}".Substring(0, 12).ToLower().Replace("_", "-");

        var first = await client.PostAsJsonAsync("/admin/subcategories",
            new { categoryKey = "Outdoors", key, labelEn = "First", labelEs = "Primero" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/admin/subcategories",
            new { categoryKey = "Outdoors", key, labelEn = "Duplicate", labelEs = "Duplicado" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Create_InvalidCategory_Returns400()
    {
        var client = CreateAdminClient();
        var response = await client.PostAsJsonAsync("/admin/subcategories",
            new { categoryKey = "InvalidCategory", key = "some-sub", labelEn = "X", labelEs = "X" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_UpdatesLabels()
    {
        // Seed a subcategory
        var db = fixture.GetDbContext();
        var id = Guid.NewGuid();
        var key = $"patch-{Guid.NewGuid():N}".Substring(0, 14).ToLower().Replace("_", "-");
        db.Subcategories.Add(new Subcategory
        {
            Id = id,
            CategoryKey = "Wellness",
            Key = key,
            LabelEn = "Old Label",
            LabelEs = "Etiqueta Vieja",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var response = await client.PatchAsJsonAsync($"/admin/subcategories/{id}",
            new { labelEn = "New Label", labelEs = "Etiqueta Nueva" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("New Label", body.GetProperty("labelEn").GetString());
        Assert.Equal("Etiqueta Nueva", body.GetProperty("labelEs").GetString());
    }

    [Fact]
    public async Task Delete_SoftDeletes_HiddenFromList_PlacesStillReadable()
    {
        // Seed a subcategory + a place that uses it
        var db = fixture.GetDbContext();
        var subId = Guid.NewGuid();
        var subKey = $"del-{Guid.NewGuid():N}".Substring(0, 12).ToLower().Replace("_", "-");
        db.Subcategories.Add(new Subcategory
        {
            Id = subId,
            CategoryKey = "Culture",
            Key = subKey,
            LabelEn = "To Delete",
            LabelEs = "A Borrar",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var placeId = Guid.NewGuid();
        db.Places.Add(new Place
        {
            Id = placeId,
            Name = $"Place With Deleted Sub {Guid.NewGuid():N}",
            Category = "Culture",
            Subcategories = new List<string> { subKey },
            City = "Miami",
            WhyThisPlace = "test place",
            Status = "published",
        });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var deleteResp = await client.DeleteAsync($"/admin/subcategories/{subId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        // Should no longer appear in list
        var listResp = await client.GetAsync("/admin/subcategories?category=Culture");
        var listBody = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        var keys = listBody.EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToList();
        Assert.DoesNotContain(subKey, keys);

        // The place still exists with the same subcategory string (not broken)
        var freshDb = fixture.GetDbContext();
        var place = await freshDb.Places.FindAsync(placeId);
        Assert.NotNull(place);
        Assert.Contains(subKey, place!.Subcategories ?? []);
    }

    private HttpClient CreateAdminClient()
    {
        var adminEmail = $"admin-subcat-{Guid.NewGuid():N}@locallist.ai";
        var adminFbUid = $"fb-admin-subcat-{Guid.NewGuid():N}";

        var db = fixture.GetDbContext();
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
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
