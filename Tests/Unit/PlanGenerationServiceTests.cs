using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Unit;

public class PlanGenerationServiceTests
{
    // ── FallbackKeyword determinism (Fix 2) ──────────────────────────────────

    [Fact]
    public void FilterByCategory_PreservesInputOrder_WhenAllMatch()
    {
        // FilterByCategory is a stable LINQ Where — output order = input order.
        // FallbackKeywordFilterAsync sorts by Id before calling FilterByCategory, so the
        // candidate pool is always identical for the same city+prefs, making same-seed
        // plans reproducible. Removing the OrderBy would break that guarantee.
        var p1 = new Place { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Category = "food",    WhyThisPlace = "t" };
        var p2 = new Place { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Category = "culture", WhyThisPlace = "t" };
        var p3 = new Place { Id = Guid.Parse("00000000-0000-0000-0000-000000000003"), Category = "food",    WhyThisPlace = "t" };

        var input = new List<Place> { p1, p2, p3 };
        var prefs = new ExtractedPreferences { Categories = new List<string> { "food" } };

        var result = PlanGenerationService.FilterByCategory(input, prefs);

        // Only food places, preserving input order
        Assert.Equal(2, result.Count);
        Assert.Equal(p1.Id, result[0].Id);
        Assert.Equal(p3.Id, result[1].Id);
    }

    [Fact]
    public void FilterByCategory_SameInputTwice_ProducesSameOutput()
    {
        // Verifies stable output — same input always → same candidate pool for the scheduler.
        var places = new List<Place>
        {
            new() { Id = Guid.Parse("aa000000-0000-0000-0000-000000000001"), Category = "food",    WhyThisPlace = "t" },
            new() { Id = Guid.Parse("bb000000-0000-0000-0000-000000000002"), Category = "outdoors", WhyThisPlace = "t" },
            new() { Id = Guid.Parse("cc000000-0000-0000-0000-000000000003"), Category = "food",    WhyThisPlace = "t" },
        };
        var prefs = new ExtractedPreferences { Categories = new List<string> { "food" } };

        var run1 = PlanGenerationService.FilterByCategory(places, prefs);
        var run2 = PlanGenerationService.FilterByCategory(places, prefs);

        Assert.Equal(run1.Select(p => p.Id), run2.Select(p => p.Id));
    }

    [Fact]
    public void FilterByCategory_EmptyCategories_ReturnsAllPlaces()
    {
        var places = new List<Place>
        {
            new() { Id = Guid.NewGuid(), Category = "food",    WhyThisPlace = "t" },
            new() { Id = Guid.NewGuid(), Category = "culture", WhyThisPlace = "t" },
        };
        var prefs = new ExtractedPreferences { Categories = new List<string>() };

        var result = PlanGenerationService.FilterByCategory(places, prefs);

        Assert.Equal(2, result.Count);
    }
}
