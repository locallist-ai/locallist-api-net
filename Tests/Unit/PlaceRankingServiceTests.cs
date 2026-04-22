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
        List<string>? suitableFor = null) => new()
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
        // cand1: cosine 0.80, sin bestFor match. base score = 0.5*0.80 = 0.40
        // cand2: cosine 0.65, bestFor match perfecto → 0.5*0.65 + 0.15*1 = 0.475
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
            (a, 0.50f), // similarity 0.50
            (b, 0.30f), // similarity 0.70 — gana
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
}
