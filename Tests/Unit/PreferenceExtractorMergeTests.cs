using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.Tests.Unit.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalList.API.Tests.Unit;

/// <summary>
/// Tests de <see cref="PreferenceExtractorService.MergeContextIntoPrefs"/> para el
/// fix de calidad de planes (fix/plan-quality-params):
///   - Categorías explícitas del wizard REEMPLAZAN (no unen) las del LLM.
///   - Budget tier del wizard llega a prefs (antes se descartaba por completo).
/// </summary>
public class PreferenceExtractorMergeTests
{
    private static PreferenceExtractorService Svc() =>
        new(StubLlmClient.Succeeding("gemini"), NullLogger<PreferenceExtractorService>.Instance);

    // ── Categorías: replace, no union ────────────────────────────────────────

    [Fact]
    public void Merge_ExplicitCategories_ReplaceLlmHallucinations()
    {
        // El LLM alucinó nightlife+shopping; el usuario eligió solo food en el wizard.
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string> { "nightlife", "shopping" },
        };
        var ctx = new TripContextDto
        {
            City = "Miami", Days = 1, GroupType = "couple",
            Categories = new List<string> { "food" },
        };

        var merged = Svc().MergeContextIntoPrefs(prefs, ctx);

        Assert.Equal(new[] { "food" }, merged.Categories);
        Assert.True(merged.CategoriesExplicit);
    }

    [Fact]
    public void Merge_ExplicitCategories_DedupesAndDropsBlanks()
    {
        var prefs = new ExtractedPreferences { Categories = new List<string> { "culture" } };
        var ctx = new TripContextDto
        {
            City = "Miami",
            Categories = new List<string> { "food", "Food", " ", "coffee" },
        };

        var merged = Svc().MergeContextIntoPrefs(prefs, ctx);

        Assert.Equal(2, merged.Categories.Count);
        Assert.Contains("food", merged.Categories);
        Assert.Contains("coffee", merged.Categories);
    }

    [Fact]
    public void Merge_NoContextCategories_KeepsLlmCategories_NotExplicit()
    {
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string> { "food", "culture" },
            CategoriesExplicit = true, // aunque venga forzado (p. ej. JSON inyectado), el merge lo resetea
        };
        var ctx = new TripContextDto { City = "Miami", Days = 2, GroupType = "couple" };

        var merged = Svc().MergeContextIntoPrefs(prefs, ctx);

        Assert.Equal(new[] { "food", "culture" }, merged.Categories);
        Assert.False(merged.CategoriesExplicit);
    }

    [Fact]
    public void Merge_FamilyGroup_RemovesNightlifeEvenIfExplicit()
    {
        var prefs = new ExtractedPreferences();
        var ctx = new TripContextDto
        {
            City = "Miami", GroupType = "family-kids",
            Categories = new List<string> { "nightlife", "outdoors" },
        };

        var merged = Svc().MergeContextIntoPrefs(prefs, ctx);

        Assert.Equal(new[] { "outdoors" }, merged.Categories);
        Assert.True(merged.CategoriesExplicit);
    }

    // ── Budget tier: antes se descartaba ─────────────────────────────────────

    [Theory]
    [InlineData("budget")]
    [InlineData("moderate")]
    [InlineData("premium")]
    [InlineData("Premium")] // case-insensitive, se normaliza a lowercase
    public void Merge_BudgetTier_MapsFromContext(string tier)
    {
        var ctx = new TripContextDto { City = "Miami", Days = 1, Budget = tier };

        var merged = Svc().MergeContextIntoPrefs(new ExtractedPreferences(), ctx);

        Assert.Equal(tier.ToLowerInvariant(), merged.BudgetTier);
    }

    [Theory]
    [InlineData("luxury")] // fuera de whitelist
    [InlineData("")]
    [InlineData(null)]
    public void Merge_InvalidBudgetTier_Ignored(string? tier)
    {
        var ctx = new TripContextDto { City = "Miami", Days = 1, Budget = tier };

        var merged = Svc().MergeContextIntoPrefs(new ExtractedPreferences(), ctx);

        Assert.Null(merged.BudgetTier);
    }

    [Fact]
    public void Merge_BudgetAmountAndTier_BothCarried()
    {
        // El wizard puede enviar tier + amount custom; ambos viajan (el ranker
        // prioriza amount por ser más fino).
        var ctx = new TripContextDto { City = "Miami", Budget = "premium", BudgetAmount = 250 };

        var merged = Svc().MergeContextIntoPrefs(new ExtractedPreferences(), ctx);

        Assert.Equal(250, merged.BudgetAmount);
        Assert.Equal("premium", merged.BudgetTier);
    }
}
