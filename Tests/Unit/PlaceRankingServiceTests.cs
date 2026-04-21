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
        int? aiVibeScore = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Category = category,
        City = "Miami",
        WhyThisPlace = "t",
        Neighborhood = neighborhood,
        BestFor = bestFor,
        AiVibeScore = aiVibeScore,
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
        var first = P("First", neighborhood: "Wynwood");
        var second = P("Second", neighborhood: "Wynwood");
        var third = P("Third", neighborhood: "Wynwood");
        var outsider = P("Outsider", neighborhood: "SouthBeach");

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
}
