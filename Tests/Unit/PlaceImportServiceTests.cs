using LocalList.API.NET.Features.Admin.Places;

namespace LocalList.API.Tests.Unit;

/// <summary>
/// Unit tests for PlaceImportService dedup helpers.
/// These test pure static logic — no DB required.
/// </summary>
public class PlaceImportServiceTests
{
    // ── IsNameCityDuplicate ───────────────────────────────────────────────

    [Fact]
    public void IsNameCityDuplicate_WhenNameAndCityMatch_ReturnsTrue()
    {
        var req = new CreatePlaceRequest { Name = "Ramen Bar", City = "Miami", Category = "Food" };
        var existing = new HashSet<(string, string)> { ("ramen bar", "miami") };

        Assert.True(PlaceImportService.IsNameCityDuplicate(req, existing));
    }

    [Fact]
    public void IsNameCityDuplicate_WhenNoMatch_ReturnsFalse()
    {
        var req = new CreatePlaceRequest { Name = "New Place", City = "Miami", Category = "Food" };
        var existing = new HashSet<(string, string)> { ("other place", "miami") };

        Assert.False(PlaceImportService.IsNameCityDuplicate(req, existing));
    }

    [Theory]
    [InlineData("RAMEN BAR", "MIAMI")]
    [InlineData("Ramen Bar", "miami")]
    [InlineData("ramen bar", "MIAMI")]
    public void IsNameCityDuplicate_CaseInsensitive(string name, string city)
    {
        // Dedup key must normalise to lowercase — mixed-case re-imports should be caught.
        var req = new CreatePlaceRequest { Name = name, City = city, Category = "Food" };
        var existing = new HashSet<(string, string)> { ("ramen bar", "miami") };

        Assert.True(PlaceImportService.IsNameCityDuplicate(req, existing));
    }

    [Fact]
    public void IsNameCityDuplicate_NullCity_TreatedAsMiami()
    {
        // When City is null, the dedup key defaults to "Miami" — same as the insert default.
        var req = new CreatePlaceRequest { Name = "Ramen Bar", City = null, Category = "Food" };
        var existing = new HashSet<(string, string)> { ("ramen bar", "miami") };

        Assert.True(PlaceImportService.IsNameCityDuplicate(req, existing));
    }

    [Fact]
    public void IsNameCityDuplicate_WithGooglePlaceId_AlwaysReturnsFalse()
    {
        // Requests with a GooglePlaceId skip Name+City dedup — they're deduped by GooglePlaceId
        // in InsertWithDedupAsync instead. This prevents false positives when the same real-world
        // place exists with a slightly different curated name.
        var req = new CreatePlaceRequest { Name = "Ramen Bar", City = "Miami", Category = "Food", GooglePlaceId = "ChIJ123" };
        var existing = new HashSet<(string, string)> { ("ramen bar", "miami") };

        Assert.False(PlaceImportService.IsNameCityDuplicate(req, existing));
    }

    [Fact]
    public void IsNameCityDuplicate_LeadingTrailingSpaces_Trimmed()
    {
        var req = new CreatePlaceRequest { Name = "  Ramen Bar  ", City = "  Miami  ", Category = "Food" };
        var existing = new HashSet<(string, string)> { ("ramen bar", "miami") };

        Assert.True(PlaceImportService.IsNameCityDuplicate(req, existing));
    }

    [Fact]
    public void IsNameCityDuplicate_EmptySet_ReturnsFalse()
    {
        var req = new CreatePlaceRequest { Name = "Ramen Bar", City = "Miami", Category = "Food" };
        var existing = new HashSet<(string, string)>();

        Assert.False(PlaceImportService.IsNameCityDuplicate(req, existing));
    }
}
