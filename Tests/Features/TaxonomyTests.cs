using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Taxonomy;
using Microsoft.Extensions.DependencyInjection;

namespace LocalList.API.Tests.Features;

public class TaxonomyTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task GetTaxonomy_PublicNoAuth_Returns200WithStructure()
    {
        var client = fixture.CreateClient();

        var response = await client.GetAsync("/taxonomy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("categories").GetArrayLength() == 7);
        Assert.True(body.TryGetProperty("subcategoriesByCategory", out var bycat));
        Assert.True(body.TryGetProperty("labels", out _));
        // Verify at least one known category has subcategories
        Assert.True(bycat.GetProperty("Food").GetArrayLength() > 0);
    }

    [Fact]
    public async Task GetTaxonomy_WithMatchingETag_Returns304()
    {
        var client = fixture.CreateClient();

        // First request to get the ETag
        var first = await client.GetAsync("/taxonomy");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        // Second request with If-None-Match
        var req = new HttpRequestMessage(HttpMethod.Get, "/taxonomy");
        req.Headers.IfNoneMatch.ParseAdd(etag!);
        var second = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task GetTaxonomy_IncludesSeededSubcategories()
    {
        // Seed a subcategory directly
        var db = fixture.GetDbContext();
        var key = $"test-sub-{Guid.NewGuid():N}".Substring(0, 20);
        db.Subcategories.Add(new Subcategory
        {
            Id = Guid.NewGuid(),
            CategoryKey = "Food",
            Key = key,
            LabelEn = "Test Sub",
            LabelEs = "Test Sub ES",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        // Invalidate server-side in-memory cache so next request re-reads from DB.
        using (var scope = fixture.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<ITaxonomyService>().Invalidate();

        var client = fixture.CreateClient();
        var response = await client.GetAsync("/taxonomy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var foodSubs = body.GetProperty("subcategoriesByCategory").GetProperty("Food")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(key, foodSubs);

        // Verify label appears in EN labels
        var labelKey = $"Food.{key}";
        var enLabels = body.GetProperty("labels").GetProperty("en");
        Assert.Equal("Test Sub", enLabels.GetProperty(labelKey).GetString());
    }
}
