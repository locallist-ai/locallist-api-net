using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalList.API.Tests.Unit;

/// <summary>
/// Unit tests for PR 3 — refinement filters in SchedulingService:
/// exclusions, dietary, pace clamp, family rule, vibesPrimary.
/// </summary>
public class SchedulingServiceRefinementsTests
{
    private static SchedulingService Service() =>
        new(NullLogger<SchedulingService>.Instance);

    private static Place MakePlace(string category, List<string>? suitableFor = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"Place-{category}",
        Category = category,
        City = "Miami",
        Status = "published",
        BestTime = "any",
        WhyThisPlace = "Test",
        SuitableFor = suitableFor,
        GooglePlaceId = Guid.NewGuid().ToString("N")[..20],
    };

    private static ExtractedPreferences DefaultPrefs(int days = 1) => new()
    {
        Days = days,
        MaxStopsPerDay = 5,
        GroupType = "couple",
        Categories = ["food"],
    };

    // ── ApplyRefinements — Exclusions ─────────────────────────────────────────

    [Fact]
    public void ApplyRefinements_ExcludesNightlife_WhenRequested()
    {
        var svc = Service();
        var places = new List<Place>
        {
            MakePlace("food"),
            MakePlace("nightlife"),
            MakePlace("culture"),
        };
        var prefs = DefaultPrefs();
        prefs.Exclusions = ["nightlife"];
        var result = new ScheduleResult();

        var filtered = svc.ApplyRefinements(places, prefs, result);

        Assert.DoesNotContain(filtered, p => p.Category == "nightlife");
        Assert.Equal(2, filtered.Count);
        Assert.Contains(result.AppliedRefinements, r => r.Contains("excluded"));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ApplyRefinements_ExclusionRemovesAll_FallsBackWithWarning()
    {
        var svc = Service();
        var places = new List<Place> { MakePlace("nightlife"), MakePlace("nightlife") };
        var prefs = DefaultPrefs();
        prefs.Exclusions = ["nightlife"];
        var result = new ScheduleResult();

        var filtered = svc.ApplyRefinements(places, prefs, result);

        Assert.Equal(2, filtered.Count); // fallback: all places returned
        Assert.Contains("exclusion_fallback", result.Warnings);
        Assert.Empty(result.AppliedRefinements);
    }

    [Fact]
    public void ApplyRefinements_NoExclusions_ReturnsSamePlaces()
    {
        var svc = Service();
        var places = new List<Place> { MakePlace("food"), MakePlace("nightlife") };
        var prefs = DefaultPrefs();
        var result = new ScheduleResult();

        var filtered = svc.ApplyRefinements(places, prefs, result);

        Assert.Equal(2, filtered.Count);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.AppliedRefinements);
    }

    [Fact]
    public void ApplyRefinements_MultipleExclusions_FiltersAll()
    {
        var svc = Service();
        var places = new List<Place>
        {
            MakePlace("food"),
            MakePlace("nightlife"),
            MakePlace("touristy"),
        };
        var prefs = DefaultPrefs();
        prefs.Exclusions = ["nightlife", "touristy"];
        var result = new ScheduleResult();

        var filtered = svc.ApplyRefinements(places, prefs, result);

        Assert.Single(filtered);
        Assert.Equal("food", filtered[0].Category);
    }

    // ── ApplyRefinements — Dietary ────────────────────────────────────────────

    [Fact]
    public void ApplyRefinements_DietaryNone_NoFiltering()
    {
        var svc = Service();
        var places = new List<Place> { MakePlace("food"), MakePlace("culture") };
        var prefs = DefaultPrefs();
        prefs.Dietary = ["none"];
        var result = new ScheduleResult();

        var filtered = svc.ApplyRefinements(places, prefs, result);

        Assert.Equal(2, filtered.Count);
        Assert.Empty(result.AppliedRefinements);
    }

    [Fact]
    public void ApplyRefinements_DietaryVegetarian_IncludesPlacesWithoutSuitableFor()
    {
        var svc = Service();
        // Places with no SuitableFor data → always included
        var places = new List<Place>
        {
            MakePlace("food"),   // no SuitableFor
            MakePlace("coffee"), // no SuitableFor
        };
        var prefs = DefaultPrefs();
        prefs.Dietary = ["vegetarian"];
        var result = new ScheduleResult();

        var filtered = svc.ApplyRefinements(places, prefs, result);

        // No SuitableFor data → all included, no refinement recorded
        Assert.Equal(2, filtered.Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ApplyRefinements_DietaryVegetarian_FiltersNonMatchingSuitableFor()
    {
        var svc = Service();
        var places = new List<Place>
        {
            MakePlace("food", suitableFor: ["vegetarian", "gluten_free"]),
            MakePlace("food", suitableFor: ["meat_lover"]), // explicitly non-vegetarian tagged
        };
        var prefs = DefaultPrefs();
        prefs.Dietary = ["vegetarian"];
        var result = new ScheduleResult();

        var filtered = svc.ApplyRefinements(places, prefs, result);

        Assert.Single(filtered);
        Assert.Contains(result.AppliedRefinements, r => r.Contains("dietary"));
    }

    [Fact]
    public void ApplyRefinements_DietaryNoMatches_FallsBackWithWarning()
    {
        var svc = Service();
        // All places explicitly tagged as non-vegan
        var places = new List<Place>
        {
            MakePlace("food", suitableFor: ["meat_lover"]),
            MakePlace("food", suitableFor: ["seafood_only"]),
        };
        var prefs = DefaultPrefs();
        prefs.Dietary = ["vegan"];
        var result = new ScheduleResult();

        var filtered = svc.ApplyRefinements(places, prefs, result);

        Assert.Equal(2, filtered.Count); // graceful fallback
        Assert.Contains("dietary_no_matches", result.Warnings);
        Assert.Empty(result.AppliedRefinements);
    }

    [Fact]
    public void ApplyRefinements_NoDietary_NoFiltering()
    {
        var svc = Service();
        var places = new List<Place>
        {
            MakePlace("food", suitableFor: ["meat_lover"]),
        };
        var prefs = DefaultPrefs();
        var result = new ScheduleResult();

        var filtered = svc.ApplyRefinements(places, prefs, result);

        Assert.Single(filtered);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.AppliedRefinements);
    }

    // ── Pace clamp via MergeContextIntoPrefs ─────────────────────────────────

    [Fact]
    public void Pace_Slow_ClampsMaxStopsTo3()
    {
        var result = new ScheduleResult();
        var svc = Service();

        var places = Enumerable.Range(0, 10).Select(_ => MakePlace("food")).ToList();
        var prefs = new ExtractedPreferences
        {
            Days = 1,
            MaxStopsPerDay = 5,
            GroupType = "solo",
            Categories = ["food"],
            Pace = "slow",
        };

        var schedule = svc.BuildPlanSchedule(places, prefs);

        Assert.True(schedule.Stops.Count <= 3,
            $"Expected ≤3 stops for slow pace, got {schedule.Stops.Count}");
    }

    [Fact]
    public void Pace_Fast_AllowsMoreThan3Stops()
    {
        var result = new ScheduleResult();
        var svc = Service();

        var places = Enumerable.Range(0, 10).Select(_ => MakePlace("food")).ToList();
        var prefs = new ExtractedPreferences
        {
            Days = 1,
            MaxStopsPerDay = 6,
            GroupType = "solo",
            Categories = ["food"],
            Pace = "fast",
        };

        var schedule = svc.BuildPlanSchedule(places, prefs);

        Assert.True(schedule.Stops.Count > 3,
            $"Expected >3 stops for fast pace, got {schedule.Stops.Count}");
    }

    // ── Family rule (pre-existing, now also tested with ApplyRefinements) ─────

    [Fact]
    public void IsGoodTimeMatch_Family_ExcludesNightlifeStrict()
    {
        var svc = Service();
        var nightlifePlace = MakePlace("nightlife");
        var prefs = new ExtractedPreferences
        {
            Days = 1, GroupType = "family", MaxStopsPerDay = 5, Categories = ["food"]
        };

        var match = svc.IsGoodTimeMatch(nightlifePlace, "evening", prefs, strict: true);

        Assert.False(match);
    }

    [Fact]
    public void IsGoodTimeMatch_Couple_NightlifeAllowed()
    {
        var svc = Service();
        var nightlifePlace = MakePlace("nightlife");
        var prefs = new ExtractedPreferences
        {
            Days = 1, GroupType = "couple", MaxStopsPerDay = 5, Categories = ["nightlife"]
        };

        var match = svc.IsGoodTimeMatch(nightlifePlace, "evening", prefs, strict: true);

        Assert.True(match);
    }

    // ── VibesPrimary through MergeContextIntoPrefs (via AiProviderService) ───
    // These are integration-level — tested via ChatAgentServiceTests.SlotsToTripContext
    // The AiProviderService.MergeContextIntoPrefs path is covered by existing builder tests.
}
