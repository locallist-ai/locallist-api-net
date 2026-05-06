using LocalList.API.NET.Features.Builder;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Unit;

public class PlaceRankingServiceTests
{
    private static Place P(
        string name,
        string category = "Food",
        string? neighborhood = null,
        List<string>? bestFor = null,
        int? aiVibeScore = null,
        List<string>? suitableFor = null,
        string? subcategory = null,
        string? priceRange = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Category = category,
        City = "Miami",
        WhyThisPlace = "t",
        Neighborhood = neighborhood,
        BestFor = bestFor,
        AiVibeScore = aiVibeScore,
        SuitableFor = suitableFor,
        Subcategory = subcategory,
        PriceRange = priceRange,
    };

    [Fact]
    public void Rank_EmptyInput_ReturnsEmpty()
    {
        var svc = new PlaceRankingService();
        var result = svc.Rank(Array.Empty<(Place, float)>(), new ExtractedPreferences());
        Assert.Empty(result);
    }

    [Fact]
    public void Rank_HighCosineAndCategoryMatch_LeadsOverOthers()
    {
        var svc = new PlaceRankingService();
        var winner = P("Winner", category: "Food");
        var loser = P("Loser", category: "Culture");
        var prefs = new ExtractedPreferences { Categories = new List<string> { "food" } };

        var ranked = svc.Rank(new[]
        {
            (winner, 0.05f), // cosine similarity 0.95
            (loser, 0.10f),  // cosine 0.90 pero mala categoría
        }, prefs);

        Assert.Equal(winner.Id, ranked[0].Id);
        Assert.Equal(loser.Id, ranked[1].Id);
    }

    [Fact]
    public void Rank_BestForMatch_OvertakesHigherCosineWithoutMatch()
    {
        var svc = new PlaceRankingService();
        // cand1: cosine 0.80, sin bestFor match. base score ≈ WeightCosine*0.80 = 0.32
        // cand2: cosine 0.65, bestFor match perfecto → WeightCosine*0.65 + WeightVibes*1
        // El WeightVibes domina a la diferencia de cosine entre 0.80 y 0.65.
        var cosineLeader = P("CosineLeader");
        var vibeLeader = P("VibeLeader", bestFor: new List<string> { "romantic" });
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string>(),
            Vibes = new List<string> { "romantic" },
        };

        var ranked = svc.Rank(new[]
        {
            (cosineLeader, 0.20f),
            (vibeLeader, 0.35f),
        }, prefs);

        Assert.Equal(vibeLeader.Id, ranked[0].Id);
    }

    [Fact]
    public void Rank_EmptyPrefs_OnlyCosineMatters()
    {
        var svc = new PlaceRankingService();
        var a = P("A");
        var b = P("B");
        var prefs = new ExtractedPreferences { Categories = new List<string>() };

        var ranked = svc.Rank(new[]
        {
            (a, 0.50f), // cosine similarity 0.50
            (b, 0.30f), // cosine similarity 0.70 — gana por cosine
        }, prefs);

        Assert.Equal(b.Id, ranked[0].Id);
    }

    [Fact]
    public void Rank_ThreeSameNeighborhood_ThirdIsPenalized()
    {
        var svc = new PlaceRankingService();
        // IDs fijos secuenciales para que el tie-breaker por Place.Id del ranker
        // (cosine idéntico → ordena por Id asc) coincida con el orden declarativo:
        // first debe ser el PRIMERO visto por el foreach de neighborhoods.
        // Con Guid.NewGuid() aleatorios el test era flaky (~1/3 pass rate).
        var first = P("First", neighborhood: "Wynwood");
        first.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = P("Second", neighborhood: "Wynwood");
        second.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var third = P("Third", neighborhood: "Wynwood");
        third.Id = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var outsider = P("Outsider", neighborhood: "SouthBeach");
        outsider.Id = Guid.Parse("00000000-0000-0000-0000-000000000004");

        // All cosine idénticos para aislar la señal de penalty.
        var prefs = new ExtractedPreferences { Categories = new List<string>() };
        var scored = svc.RankWithScores(new[]
        {
            (first, 0.10f),
            (second, 0.10f),
            (third, 0.10f),
            (outsider, 0.10f),
        }, prefs);

        // El primer place de cada neighborhood no penaliza (penalty = 0).
        // El resto mismos neighborhood sí (penalty = 1 → -0.05 sobre score).
        var firstScored = scored.Single(s => s.Place.Id == first.Id);
        var outsiderScored = scored.Single(s => s.Place.Id == outsider.Id);
        var penalizedRepeats = scored.Where(s => s.Breakdown.NeighborhoodPenalty > 0).ToList();

        Assert.Equal(0f, firstScored.Breakdown.NeighborhoodPenalty);
        Assert.Equal(0f, outsiderScored.Breakdown.NeighborhoodPenalty);
        Assert.Equal(2, penalizedRepeats.Count); // second y third
        Assert.True(penalizedRepeats[0].Score < firstScored.Score);
    }

    [Fact]
    public void Rank_AiVibeScore_InfluencesOrderAtParity()
    {
        var svc = new PlaceRankingService();
        var highVibe = P("HighVibe", aiVibeScore: 90);
        var lowVibe = P("LowVibe", aiVibeScore: 10);
        var prefs = new ExtractedPreferences { Categories = new List<string>() };

        var ranked = svc.Rank(new[]
        {
            (highVibe, 0.10f),
            (lowVibe, 0.10f),
        }, prefs);

        Assert.Equal(highVibe.Id, ranked[0].Id);
    }

    // ── SuitableFor (Parte C) ────────────────────────────────────────────────

    [Fact]
    public void ScoreSuitableFor_FamilyGroup_MatchesKidsSuitable_Returns1()
    {
        var svc = new PlaceRankingService();
        var kidsPlace = P("KidsPark", suitableFor: new List<string> { "kids", "family" });
        var prefs = new ExtractedPreferences
        {
            GroupType = "family-kids",
            Categories = new List<string>(),
        };

        var scored = svc.RankWithScores(new[] { (kidsPlace, 0.20f) }, prefs);
        Assert.Equal(1f, scored[0].Breakdown.SuitableForMatch);
    }

    [Fact]
    public void ScoreSuitableFor_FamilyGroup_PlaceIsAdultsOnly_Returns0()
    {
        var svc = new PlaceRankingService();
        var adultsOnly = P("AdultsBar", category: "nightlife", suitableFor: new List<string> { "adults-only" });
        var prefs = new ExtractedPreferences
        {
            GroupType = "family-kids",
            Categories = new List<string>(),
        };

        var scored = svc.RankWithScores(new[] { (adultsOnly, 0.20f) }, prefs);
        Assert.Equal(0f, scored[0].Breakdown.SuitableForMatch);
    }

    [Fact]
    public void ScoreSuitableFor_NoGroupTypeHint_ReturnsNeutral1()
    {
        var svc = new PlaceRankingService();
        var place = P("NoHint", suitableFor: new List<string> { "adults-only" });
        var prefs = new ExtractedPreferences
        {
            GroupType = "",
            Categories = new List<string>(),
        };

        var scored = svc.RankWithScores(new[] { (place, 0.20f) }, prefs);
        // Sin groupType, el scorer no castiga — devuelve 1.0 neutral.
        Assert.Equal(1f, scored[0].Breakdown.SuitableForMatch);
    }

    [Fact]
    public void ScoreSuitableFor_NullSuitableFor_ReturnsPointFive()
    {
        var svc = new PlaceRankingService();
        var place = P("Untagged"); // suitableFor null por defecto
        var prefs = new ExtractedPreferences
        {
            GroupType = "family-kids",
            Categories = new List<string>(),
        };

        var scored = svc.RankWithScores(new[] { (place, 0.20f) }, prefs);
        // No hay etiquetas suitable_for en el catálogo → no-info neutral-ish.
        Assert.Equal(0.5f, scored[0].Breakdown.SuitableForMatch);
    }

    [Fact]
    public void Rank_FamilyGroup_ExcludesAdultsOnlyFromTop()
    {
        var svc = new PlaceRankingService();
        // Adults-only con cosine muy alto (0.95) DEBE quedar por debajo de un family
        // con cosine peor (0.80), porque SuitableFor=0 le resta el peso entero (0.15).
        var adultsOnly = P("AdultsLounge", category: "nightlife",
            suitableFor: new List<string> { "adults-only" });
        var familyFriendly = P("FamilyCafe", category: "coffee",
            suitableFor: new List<string> { "family" });
        var prefs = new ExtractedPreferences
        {
            GroupType = "family-kids",
            Categories = new List<string>(),
        };

        var ranked = svc.Rank(new[]
        {
            (adultsOnly, 0.05f),   // cosine 0.95
            (familyFriendly, 0.20f), // cosine 0.80
        }, prefs);

        Assert.Equal(familyFriendly.Id, ranked[0].Id);
        Assert.Equal(adultsOnly.Id, ranked[1].Id);
    }

    // ── Soft signals: Subcategory, CompanyTags, StyleTags, Budget ────────────
    // B1 (audit follow-up 2026-04-27): Pablo's "no breaking, regression test
    // mandatory" rule for soft-signals. Each signal: at least 1 absent (legacy
    // preserved) + 1 present (expected delta) test.

    [Fact]
    public void ScoreSubcategoryMatch_NoSubcategoriesPref_ReturnsZero()
    {
        var svc = new PlaceRankingService();
        var place = P("Sushi", category: "Food", subcategory: "Sushi");
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string> { "food" },
            Subcategories = null, // legacy: no drill-down
        };

        var scored = svc.RankWithScores(new[] { (place, 0.20f) }, prefs);
        Assert.Equal(0f, scored[0].Breakdown.SubcategoryMatch);
    }

    [Fact]
    public void ScoreSubcategoryMatch_BucketMatchesBySubstring_ReturnsOne()
    {
        var svc = new PlaceRankingService();
        var place = P("Pasta", category: "Food", subcategory: "Italian");
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string> { "food" },
            Subcategories = new Dictionary<string, List<string>>
            {
                ["food"] = new() { "italian" },
            },
        };

        var scored = svc.RankWithScores(new[] { (place, 0.20f) }, prefs);
        Assert.Equal(1f, scored[0].Breakdown.SubcategoryMatch);
    }

    [Fact]
    public void ScoreSubcategoryMatch_BucketWithoutMatch_ReturnsZero()
    {
        var svc = new PlaceRankingService();
        var place = P("Mex", category: "Food", subcategory: "Tacos");
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string> { "food" },
            Subcategories = new Dictionary<string, List<string>>
            {
                ["food"] = new() { "sushi", "italian" },
            },
        };

        var scored = svc.RankWithScores(new[] { (place, 0.20f) }, prefs);
        Assert.Equal(0f, scored[0].Breakdown.SubcategoryMatch);
    }

    [Fact]
    public void ScoreCompanyTagsMatch_EmptyTags_ReturnsZero()
    {
        var svc = new PlaceRankingService();
        var place = P("Spot", suitableFor: new List<string> { "honeymoon" });
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string>(),
            CompanyTags = null,
        };

        var scored = svc.RankWithScores(new[] { (place, 0.20f) }, prefs);
        Assert.Equal(0f, scored[0].Breakdown.CompanyTagsMatch);
    }

    [Fact]
    public void ScoreCompanyTagsMatch_HoneymoonInSuitableFor_ReturnsOne()
    {
        var svc = new PlaceRankingService();
        var place = P("Romantic", suitableFor: new List<string> { "honeymoon", "anniversary" });
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string>(),
            CompanyTags = new List<string> { "honeymoon" },
        };

        var scored = svc.RankWithScores(new[] { (place, 0.20f) }, prefs);
        Assert.Equal(1f, scored[0].Breakdown.CompanyTagsMatch);
    }

    [Fact]
    public void ScoreStyleTagsMatch_EmptyTags_ReturnsZero()
    {
        var svc = new PlaceRankingService();
        var place = P("Spot", bestFor: new List<string> { "urban-explorer", "foodie" });
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string>(),
            StyleTags = null,
        };

        var scored = svc.RankWithScores(new[] { (place, 0.20f) }, prefs);
        Assert.Equal(0f, scored[0].Breakdown.StyleTagsMatch);
    }

    [Fact]
    public void ScoreStyleTagsMatch_PartialOverlap_ReturnsProportional()
    {
        var svc = new PlaceRankingService();
        // place tiene foodie pero no urban → 1 of 2 prefs matches → 0.5
        var place = P("Foodie", bestFor: new List<string> { "foodie" });
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string>(),
            StyleTags = new List<string> { "urban", "foodie" },
        };

        var scored = svc.RankWithScores(new[] { (place, 0.20f) }, prefs);
        Assert.Equal(0.5f, scored[0].Breakdown.StyleTagsMatch);
    }

    [Fact]
    public void ScoreStyleTagsMatch_FullOverlap_ReturnsOne()
    {
        var svc = new PlaceRankingService();
        var place = P("Both", bestFor: new List<string> { "urban-explorer", "foodie" });
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string>(),
            StyleTags = new List<string> { "urban", "foodie" },
        };

        var scored = svc.RankWithScores(new[] { (place, 0.20f) }, prefs);
        Assert.Equal(1f, scored[0].Breakdown.StyleTagsMatch);
    }

    [Fact]
    public void ScoreBudgetMatch_NoBudgetAmount_ReturnsZero()
    {
        var svc = new PlaceRankingService();
        var place = P("Spot", priceRange: "$$");
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string>(),
            BudgetAmount = null,
        };

        var scored = svc.RankWithScores(new[] { (place, 0.20f) }, prefs);
        Assert.Equal(0f, scored[0].Breakdown.BudgetMatch);
    }

    [Fact]
    public void ScoreBudgetMatch_AmountSetButPlacePriceRangeEmpty_ReturnsHalf()
    {
        var svc = new PlaceRankingService();
        var place = P("Spot", priceRange: null);
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string>(),
            BudgetAmount = 150,
        };

        var scored = svc.RankWithScores(new[] { (place, 0.20f) }, prefs);
        Assert.Equal(0.5f, scored[0].Breakdown.BudgetMatch);
    }

    [Fact]
    public void ScoreBudgetMatch_TierExact_ReturnsOne()
    {
        var svc = new PlaceRankingService();
        // amount 150 → desiredTier 2 ($$), place "$$" → exact match
        var place = P("Mid", priceRange: "$$");
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string>(),
            BudgetAmount = 150,
        };

        var scored = svc.RankWithScores(new[] { (place, 0.20f) }, prefs);
        Assert.Equal(1f, scored[0].Breakdown.BudgetMatch);
    }

    [Fact]
    public void ScoreBudgetMatch_TierAdjacent_ReturnsPointSix()
    {
        var svc = new PlaceRankingService();
        // amount 150 → desiredTier 2 ($$), place "$$$" → diff 1 → 0.6
        var place = P("Up", priceRange: "$$$");
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string>(),
            BudgetAmount = 150,
        };

        var scored = svc.RankWithScores(new[] { (place, 0.20f) }, prefs);
        Assert.Equal(0.6f, scored[0].Breakdown.BudgetMatch);
    }

    [Fact]
    public void ScoreBudgetMatch_TierFar_ReturnsZero()
    {
        var svc = new PlaceRankingService();
        // amount 50 → desiredTier 1 ($), place "$$$$" → diff 3 → 0
        var place = P("Premium", priceRange: "$$$$");
        var prefs = new ExtractedPreferences
        {
            Categories = new List<string>(),
            BudgetAmount = 50,
        };

        var scored = svc.RankWithScores(new[] { (place, 0.20f) }, prefs);
        Assert.Equal(0f, scored[0].Breakdown.BudgetMatch);
    }
}
