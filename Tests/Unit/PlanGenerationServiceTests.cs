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

    // ── ApplyCategoryGate (fix/plan-quality-params) ──────────────────────────
    // Las categorías elegidas actúan como filtro duro; solo se rellena con otras
    // categorías cuando el catálogo no da para llenar el plan (fallback graceful).

    private static Place Pl(string category, string suffix = "") => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{category}{suffix}",
        Category = category,
        WhyThisPlace = "t",
    };

    [Fact]
    public void ApplyCategoryGate_EnoughMatches_ExcludesOtherCategories()
    {
        // 4 food + 2 culture, plan de 1 día × 3 stops → 4 food ≥ 3 needed → gate duro.
        var places = new List<Place>
        {
            Pl("food", "1"), Pl("culture", "1"), Pl("food", "2"),
            Pl("food", "3"), Pl("culture", "2"), Pl("food", "4"),
        };
        var prefs = new ExtractedPreferences
        {
            Days = 1, MaxStopsPerDay = 3,
            Categories = new List<string> { "food" },
        };

        var result = PlanGenerationService.ApplyCategoryGate(places, prefs);

        Assert.Equal(4, result.Count);
        Assert.All(result, p => Assert.Equal("food", p.Category));
    }

    [Fact]
    public void ApplyCategoryGate_TooFewMatches_TopsUpWithRest_CategoryFirst()
    {
        // Solo 2 food para un plan que necesita 5 → completa con culture,
        // pero los food van primero (prioridad de la categoría pedida).
        var food1 = Pl("food", "1");
        var food2 = Pl("food", "2");
        var places = new List<Place> { Pl("culture", "1"), food1, Pl("culture", "2"), food2 };
        var prefs = new ExtractedPreferences
        {
            Days = 1, MaxStopsPerDay = 5,
            Categories = new List<string> { "food" },
        };

        var result = PlanGenerationService.ApplyCategoryGate(places, prefs);

        Assert.Equal(4, result.Count); // nadie se pierde
        Assert.Equal(food1.Id, result[0].Id);
        Assert.Equal(food2.Id, result[1].Id);
        Assert.All(result.Skip(2), p => Assert.Equal("culture", p.Category));
    }

    [Fact]
    public void ApplyCategoryGate_NoCategories_ReturnsInputUnchanged()
    {
        var places = new List<Place> { Pl("food"), Pl("culture") };
        var prefs = new ExtractedPreferences { Days = 1, MaxStopsPerDay = 3, Categories = new List<string>() };

        var result = PlanGenerationService.ApplyCategoryGate(places, prefs);

        Assert.Equal(places.Select(p => p.Id), result.Select(p => p.Id));
    }

    // ── ComputeRequestSeed (fix/plan-quality-params) ─────────────────────────
    // La semilla deriva del request: misma petición → mismo plan (reproducible);
    // cambiar cualquier parámetro elegido por el usuario → semilla distinta.

    private static TripContextDto Ctx() => new()
    {
        City = "Miami",
        Days = 2,
        GroupType = "couple",
        Budget = "premium",
        Categories = new List<string> { "food" },
    };

    [Fact]
    public void ComputeRequestSeed_SameRequest_SameSeed()
    {
        var s1 = PlanGenerationService.ComputeRequestSeed("romantic dinner", "Miami", "en", Ctx());
        var s2 = PlanGenerationService.ComputeRequestSeed("romantic dinner", "Miami", "en", Ctx());

        Assert.Equal(s1, s2);
        Assert.True(s1 >= 0, "seed debe ser no-negativa (se usa como Random seed)");
    }

    [Theory]
    [InlineData("message")]
    [InlineData("days")]
    [InlineData("budget")]
    [InlineData("categories")]
    [InlineData("pace")]
    public void ComputeRequestSeed_DifferentParam_DifferentSeed(string param)
    {
        var baseline = PlanGenerationService.ComputeRequestSeed("romantic dinner", "Miami", "en", Ctx());

        var ctx = Ctx();
        var message = "romantic dinner";
        switch (param)
        {
            case "message":    message = "family brunch"; break;
            case "days":       ctx.Days = 3; break;
            case "budget":     ctx.Budget = "budget"; break;
            case "categories": ctx.Categories = new List<string> { "culture" }; break;
            case "pace":       ctx.Pace = "slow"; break;
        }
        var variant = PlanGenerationService.ComputeRequestSeed(message, "Miami", "en", ctx);

        Assert.NotEqual(baseline, variant);
    }
}
