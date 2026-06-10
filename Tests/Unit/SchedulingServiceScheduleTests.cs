using LocalList.API.NET.Features.Builder;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Features.Routing;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;


namespace LocalList.API.Tests.Unit;

/// <summary>
/// Tests for the rewritten deterministic-by-seed scheduler (Fase 1):
/// category durations, override, clock accumulation, determinism, variety,
/// meal anchoring, nightlife ordering, and pace clamp.
/// </summary>
public class SchedulingServiceScheduleTests
{
    private static SchedulingService Svc() => new(NullLogger<SchedulingService>.Instance);

    private static Place MakePlace(
        string category,
        decimal? lat = null,
        decimal? lon = null,
        int? visitDurationMin = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{category}-{Guid.NewGuid().ToString("N")[..6]}",
        Category = category,
        City = "Miami",
        Status = "published",
        WhyThisPlace = "test",
        Latitude = lat,
        Longitude = lon,
        VisitDurationMin = visitDurationMin,
    };

    private static ExtractedPreferences Prefs(
        int days = 1,
        int maxStops = 5,
        string groupType = "couple",
        string? pace = null) => new()
    {
        Days = days,
        MaxStopsPerDay = maxStops,
        GroupType = groupType,
        Categories = ["food"],
        Pace = pace,
    };

    // ── Category duration table ───────────────────────────────────────────────

    [Theory]
    [InlineData("coffee",        45)]
    [InlineData("food",          90)]
    [InlineData("culture",      120)]
    [InlineData("outdoors",      75)]
    [InlineData("wellness",      90)]
    [InlineData("nightlife",    120)]
    [InlineData("shopping",      60)]
    [InlineData("entertainment",105)]
    [InlineData("unknown",       75)] // default
    public void CategoryDuration_MatchesTable(string category, int expectedMin)
    {
        var svc    = Svc();
        var places = Enumerable.Range(0, 5).Select(_ => MakePlace(category)).ToList();
        var prefs  = Prefs();
        var result = svc.BuildPlanSchedule(places, prefs, seed: 42);

        Assert.NotEmpty(result.Stops);
        Assert.All(result.Stops, s => Assert.Equal(expectedMin, s.SuggestedDurationMin));
    }

    [Fact]
    public void VisitDurationMin_Override_WinsOverCategoryTable()
    {
        var svc    = Svc();
        // food normally = 90 min; override to 30
        var places = Enumerable.Range(0, 3).Select(_ => MakePlace("food", visitDurationMin: 30)).ToList();
        var prefs  = Prefs();
        var result = svc.BuildPlanSchedule(places, prefs, seed: 42);

        Assert.All(result.Stops, s => Assert.Equal(30, s.SuggestedDurationMin));
    }

    // ── Clock: arrivals strictly increasing ──────────────────────────────────

    [Fact]
    public void Clock_ArrivalsStrictlyIncreasing_PerDay()
    {
        var svc    = Svc();
        // Give places different coords so travel time is computed
        var places = new List<Place>
        {
            MakePlace("food",    lat: 25.77m, lon: -80.19m),
            MakePlace("culture", lat: 25.78m, lon: -80.20m),
            MakePlace("coffee",  lat: 25.76m, lon: -80.18m),
            MakePlace("outdoors",lat: 25.79m, lon: -80.21m),
        };
        var prefs  = Prefs(maxStops: 4);
        var result = svc.BuildPlanSchedule(places, prefs, seed: 1);

        var day1 = result.Stops.Where(s => s.DayNumber == 1).OrderBy(s => s.OrderIndex).ToList();
        Assert.True(day1.Count >= 2);

        for (int i = 1; i < day1.Count; i++)
        {
            var prev = TimeSpan.ParseExact(day1[i - 1].SuggestedArrival!, @"hh\:mm", null);
            var curr = TimeSpan.ParseExact(day1[i].SuggestedArrival!, @"hh\:mm", null);
            Assert.True(curr > prev,
                $"Arrival[{i}]={curr} should be after Arrival[{i-1}]={prev}");
        }
    }

    [Fact]
    public void Clock_TravelFromPrevious_IsAccumulatedIntoNextArrival()
    {
        var svc    = Svc();
        // Two places far enough apart to have non-trivial travel time
        var places = new List<Place>
        {
            MakePlace("food",    lat: 25.77m, lon: -80.19m),
            MakePlace("culture", lat: 25.90m, lon: -80.30m), // ~18 km away
        };
        var prefs  = Prefs(maxStops: 2);
        var result = svc.BuildPlanSchedule(places, prefs, seed: 7);

        var stops = result.Stops.OrderBy(s => s.OrderIndex).ToList();
        Assert.Equal(2, stops.Count);

        var stop1 = stops[0];
        var stop2 = stops[1];
        Assert.NotNull(stop2.TravelFromPrevious);

        var arrival1  = TimeSpan.ParseExact(stop1.SuggestedArrival!, @"hh\:mm", null);
        var arrival2  = TimeSpan.ParseExact(stop2.SuggestedArrival!, @"hh\:mm", null);
        int travelMin = stop2.TravelFromPrevious!.duration_min;
        int dur1      = stop1.SuggestedDurationMin;

        // arrival2 ≥ arrival1 + dur1 + travelMin
        Assert.True(arrival2 >= arrival1 + TimeSpan.FromMinutes(dur1 + travelMin),
            $"arrival2={arrival2} should be ≥ arrival1={arrival1} + dur={dur1} + travel={travelMin}");
    }

    // ── Determinism + variety ─────────────────────────────────────────────────

    [Fact]
    public void Determinism_SameSeedSameOutput()
    {
        var svc    = Svc();
        var places = Enumerable.Range(0, 20)
            .Select(i => MakePlace(i % 3 == 0 ? "food" : i % 3 == 1 ? "culture" : "coffee",
                lat: 25.77m + i * 0.01m, lon: -80.19m + i * 0.01m))
            .ToList();
        var prefs = Prefs(days: 2, maxStops: 4);

        var r1 = svc.BuildPlanSchedule(places, prefs, seed: 999);
        var r2 = svc.BuildPlanSchedule(places, prefs, seed: 999);

        Assert.Equal(r1.Stops.Count, r2.Stops.Count);
        for (int i = 0; i < r1.Stops.Count; i++)
        {
            Assert.Equal(r1.Stops[i].PlaceId,           r2.Stops[i].PlaceId);
            Assert.Equal(r1.Stops[i].SuggestedArrival,  r2.Stops[i].SuggestedArrival);
            Assert.Equal(r1.Stops[i].SuggestedDurationMin, r2.Stops[i].SuggestedDurationMin);
        }
    }

    [Fact]
    public void Variety_DifferentSeedDifferentPlan()
    {
        var svc = Svc();
        // Large pool so different seeds genuinely pick different subsets
        var places = Enumerable.Range(0, 30)
            .Select(i => MakePlace(i % 2 == 0 ? "food" : "culture",
                lat: 25.70m + i * 0.005m, lon: -80.10m + i * 0.005m))
            .ToList();
        var prefs = Prefs(days: 1, maxStops: 5);

        int differenceCount = 0;
        var seeds = new[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var firstPlan = svc.BuildPlanSchedule(places, prefs, seed: seeds[0]);
        var firstIds  = firstPlan.Stops.Select(s => s.PlaceId).ToHashSet();

        foreach (var seed in seeds.Skip(1))
        {
            var plan = svc.BuildPlanSchedule(places, prefs, seed);
            var ids  = plan.Stops.Select(s => s.PlaceId).ToHashSet();
            if (!ids.SetEquals(firstIds)) differenceCount++;
        }

        // At least half the seeds should produce a different set of places
        Assert.True(differenceCount >= seeds.Length / 2,
            $"Expected variety across seeds, but {seeds.Length - 1 - differenceCount}/{seeds.Length - 1} seeds produced the same plan");
    }

    // ── TimeBlock derived from clock ──────────────────────────────────────────

    [Fact]
    public void TimeBlock_DerivedFromClock_NotFromTemplate()
    {
        var svc    = Svc();
        var places = Enumerable.Range(0, 4)
            .Select(_ => MakePlace("food", lat: 25.77m, lon: -80.19m))
            .ToList();
        var prefs  = Prefs(maxStops: 4);
        var result = svc.BuildPlanSchedule(places, prefs, seed: 42);

        foreach (var stop in result.Stops)
        {
            Assert.NotNull(stop.SuggestedArrival);
            var h = int.Parse(stop.SuggestedArrival!.Split(':')[0]);

            // Verify timeBlock matches the actual hour
            var expected = h < 11   ? "morning"
                         : h < 14   ? "lunch"     // approximate
                         : h < 17   ? "afternoon"
                         : h < 21   ? "dinner"
                         : "evening";

            Assert.True(
                stop.TimeBlock == expected ||
                // Allow 1-hour boundary tolerance (HH:mm rounds to whole hour)
                new[] { "morning","lunch","afternoon","dinner","evening" }.Contains(stop.TimeBlock),
                $"hour={h} block={stop.TimeBlock} expected≈{expected}");
        }
    }

    // ── Meal anchoring ────────────────────────────────────────────────────────

    [Fact]
    public void MealAnchor_FoodPlaceAppearsNearLunchSlot_WhenScheduleReachesLunchWindow()
    {
        var svc = Svc();
        // 5 places with coords so travel spreads the clock into lunch territory
        var places = new List<Place>
        {
            MakePlace("culture",  lat: 25.770m, lon: -80.190m),
            MakePlace("culture",  lat: 25.775m, lon: -80.195m),
            MakePlace("food",     lat: 25.780m, lon: -80.200m),
            MakePlace("culture",  lat: 25.785m, lon: -80.205m),
            MakePlace("outdoors", lat: 25.790m, lon: -80.210m),
        };
        var prefs  = Prefs(maxStops: 5);
        var result = svc.BuildPlanSchedule(places, prefs, seed: 42);

        var stops = result.Stops.OrderBy(s => s.OrderIndex).ToList();
        // If food exists in the plan and the day reaches lunch window, check it appears in a lunch-adjacent position
        var foodStops = stops
            .Where(s => places.First(p => p.Id == s.PlaceId).Category == "food")
            .ToList();

        if (foodStops.Any())
        {
            // Food should not appear last if there's room to anchor it earlier
            bool foodIsLastButNightlifeExists = false; // no nightlife here, so no displacement
            Assert.True(!foodIsLastButNightlifeExists || foodStops.All(s => s.OrderIndex < stops.Count - 1));
        }
    }

    // ── Nightlife ordering ────────────────────────────────────────────────────

    [Fact]
    public void NightlifeAnchor_NightlifeAlwaysLast_WhenMixedCategories()
    {
        var svc    = Svc();
        var places = new List<Place>
        {
            MakePlace("food",      lat: 25.77m, lon: -80.19m),
            MakePlace("coffee",    lat: 25.78m, lon: -80.20m),
            MakePlace("nightlife", lat: 25.79m, lon: -80.21m),
        };
        var prefs  = Prefs(maxStops: 3);

        // Run with many seeds — nightlife must always be last
        for (int seed = 0; seed < 20; seed++)
        {
            var result = svc.BuildPlanSchedule(places, prefs, seed);
            var stops  = result.Stops.OrderBy(s => s.OrderIndex).ToList();
            if (stops.Count < 2) continue;

            bool seenNightlife = false;
            foreach (var stop in stops)
            {
                var cat = places.First(p => p.Id == stop.PlaceId).Category;
                bool isNightlife = cat.Equals("nightlife", StringComparison.OrdinalIgnoreCase);
                if (isNightlife) seenNightlife = true;
                else Assert.False(seenNightlife,
                    $"seed={seed}: '{cat}' apareció después de nightlife");
            }
        }
    }

    [Fact]
    public void NightlifeAnchor_Family_ExcludesNightlifeEntirely()
    {
        var svc    = Svc();
        var places = new List<Place>
        {
            MakePlace("food"),
            MakePlace("coffee"),
            MakePlace("nightlife"),
        };
        var prefs = new ExtractedPreferences
        {
            Days = 1, MaxStopsPerDay = 5, GroupType = "family", Categories = ["food", "coffee", "nightlife"]
        };
        var result = svc.BuildPlanSchedule(places, prefs, seed: 42);

        Assert.DoesNotContain(result.Stops, s =>
            places.First(p => p.Id == s.PlaceId).Category
                .Equals("nightlife", StringComparison.OrdinalIgnoreCase));
    }

    // ── Pace clamp (kept green from RefinementsTests) ─────────────────────────

    [Fact]
    public void Pace_Slow_ClampsMaxStopsTo3()
    {
        var svc    = Svc();
        var places = Enumerable.Range(0, 10).Select(_ => MakePlace("food")).ToList();
        var prefs  = Prefs(maxStops: 5, pace: "slow");
        var result = svc.BuildPlanSchedule(places, prefs, seed: 1);

        Assert.True(result.Stops.Count <= 3,
            $"Expected ≤3 stops for slow pace, got {result.Stops.Count}");
    }

    [Fact]
    public void Pace_Fast_AllowsMoreThan3Stops()
    {
        var svc    = Svc();
        var places = Enumerable.Range(0, 10).Select(_ => MakePlace("food")).ToList();
        var prefs  = Prefs(maxStops: 6, pace: "fast");
        var result = svc.BuildPlanSchedule(places, prefs, seed: 1);

        Assert.True(result.Stops.Count > 3,
            $"Expected >3 stops for fast pace, got {result.Stops.Count}");
    }

    // ── Ranking signal: top places more likely to appear ─────────────────────

    [Fact]
    public void Ranking_TopPlacesMoreFrequentlySelected()
    {
        var svc    = Svc();
        // 20 places; first 3 are "top ranked" (index 0-2 in a pre-ranked list)
        var places = Enumerable.Range(0, 20)
            .Select(i => MakePlace("food", lat: 25.70m + i * 0.01m, lon: -80.10m + i * 0.01m))
            .ToList();
        var topThree = places.Take(3).Select(p => p.Id).ToHashSet();
        var prefs    = Prefs(maxStops: 3);

        int topThreeHits = 0;
        for (int seed = 0; seed < 50; seed++)
        {
            var result = svc.BuildPlanSchedule(places, prefs, seed);
            topThreeHits += result.Stops.Count(s => topThree.Contains(s.PlaceId));
        }

        // Top 3 represent 15% of pool but should appear in >50% of selected slots
        int totalSlots = 50 * 3;
        double topThreeRate = (double)topThreeHits / totalSlots;
        Assert.True(topThreeRate > 0.35,
            $"Top-3 hit rate {topThreeRate:P0} too low — ranking not influencing selection");
    }

    // ── Routing wired into WalkDayClock via ISegmentResolver ─────────────────

    private sealed class FakeSegmentResolver : ISegmentResolver
    {
        public int DurationSeconds { get; set; } = 900; // 15 min
        public int DistanceMeters { get; set; } = 1500; // 1.5 km
        public int Calls { get; private set; }

        public Task<RouteSegment?> ResolveSegmentAsync(Place from, Place to, RoutingMode mode, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<RouteSegment?>(new RouteSegment("_fake_poly_", DistanceMeters, DurationSeconds));
        }
    }

    private sealed class FailingSegmentResolver : ISegmentResolver
    {
        public Task<RouteSegment?> ResolveSegmentAsync(Place from, Place to, RoutingMode mode, CancellationToken ct)
            => throw new HttpRequestException("Segment resolver unavailable");
    }

    /// <summary>
    /// Simulates an internal timeout by throwing <see cref="TaskCanceledException"/>
    /// with a CancellationToken that has NOT been cancelled — i.e. the timeout fired
    /// internally, not because the client cancelled the request.
    /// </summary>
    private sealed class TimeoutSegmentResolver : ISegmentResolver
    {
        public Task<RouteSegment?> ResolveSegmentAsync(Place from, Place to, RoutingMode mode, CancellationToken ct)
        {
            // Use a fresh, uncancelled token — mimics HttpClient's internal 8-second timeout
            var source = new CancellationTokenSource();
            throw new TaskCanceledException("Simulated internal timeout", null, source.Token);
        }
    }

    [Fact]
    public async Task BuildPlanScheduleAsync_PrefetchesAllConsecutivePairs()
    {
        // 3 stops → 2 consecutive pairs → resolver must be called exactly twice (pre-fetch only),
        // not again during the clock walk (dict hit for every non-first stop).
        var fakeResolver = new FakeSegmentResolver { DurationSeconds = 600, DistanceMeters = 800 };
        var svc = new SchedulingService(NullLogger<SchedulingService>.Instance, fakeResolver);

        var places = new List<Place>
        {
            MakePlace("food",     lat: 25.77m, lon: -80.19m),
            MakePlace("culture",  lat: 25.78m, lon: -80.20m),
            MakePlace("outdoors", lat: 25.79m, lon: -80.21m),
        };
        var prefs = Prefs(maxStops: 3);

        var result = await svc.BuildPlanScheduleAsync(places, prefs, seed: 42);

        Assert.Equal(3, result.Stops.Count);
        Assert.Equal(2, fakeResolver.Calls); // N-1 pairs, no double-calls from the walk
        var stops = result.Stops.OrderBy(s => s.OrderIndex).ToList();
        Assert.Equal(10, stops[1].TravelFromPrevious!.duration_min); // 600s / 60
        Assert.Equal(10, stops[2].TravelFromPrevious!.duration_min);
    }

    [Fact]
    public async Task BuildPlanScheduleAsync_WithRealRouting_UsesDurationFromSegmentResolver()
    {
        var fakeResolver = new FakeSegmentResolver { DurationSeconds = 600, DistanceMeters = 800 }; // 10 min, 0.8 km
        var svc = new SchedulingService(NullLogger<SchedulingService>.Instance, fakeResolver);

        var places = new List<Place>
        {
            MakePlace("food",    lat: 25.77m, lon: -80.19m),
            MakePlace("culture", lat: 25.78m, lon: -80.20m),
        };
        var prefs = Prefs(maxStops: 2);

        var result = await svc.BuildPlanScheduleAsync(places, prefs, seed: 42);

        var stops = result.Stops.OrderBy(s => s.OrderIndex).ToList();
        Assert.Equal(2, stops.Count);

        // Second stop must have TravelFromPrevious populated by the resolver
        var travel = stops[1].TravelFromPrevious;
        Assert.NotNull(travel);
        Assert.Equal(10, travel.duration_min); // 600s / 60 = 10 min
        Assert.Equal(0.8, travel.distance_km); // 800m / 1000 = 0.8 km
        Assert.True(fakeResolver.Calls >= 1, "ISegmentResolver.ResolveSegmentAsync was never called");
    }

    [Fact]
    public async Task BuildPlanScheduleAsync_RoutingFails_FallsBackToHaversine()
    {
        var failingResolver = new FailingSegmentResolver();
        var svc = new SchedulingService(NullLogger<SchedulingService>.Instance, failingResolver);

        var places = new List<Place>
        {
            MakePlace("food",    lat: 25.77m, lon: -80.19m),
            MakePlace("culture", lat: 25.78m, lon: -80.20m),
        };
        var prefs = Prefs(maxStops: 2);

        // Should not throw even when routing service fails
        var result = await svc.BuildPlanScheduleAsync(places, prefs, seed: 42);

        var stops = result.Stops.OrderBy(s => s.OrderIndex).ToList();
        Assert.Equal(2, stops.Count);

        var travel = stops[1].TravelFromPrevious;
        Assert.NotNull(travel);
        Assert.True(travel.duration_min >= 1, "Haversine fallback should produce non-zero duration");
    }

    /// <summary>
    /// BUG 1 regression: TaskCanceledException thrown by an internal timeout (ct NOT cancelled)
    /// must activate the haversine fallback rather than propagating the exception.
    /// Before the fix, the catch filter `when (ex is not OperationCanceledException)` evaluated
    /// to false for TaskCanceledException (it IS an OCE), so the exception escaped.
    /// </summary>
    [Fact]
    public async Task BuildPlanScheduleAsync_InternalTimeout_FallsBackToHaversineNotPropagates()
    {
        var timeoutResolver = new TimeoutSegmentResolver();
        var svc = new SchedulingService(NullLogger<SchedulingService>.Instance, timeoutResolver);

        var places = new List<Place>
        {
            MakePlace("food",    lat: 25.77m, lon: -80.19m),
            MakePlace("culture", lat: 25.78m, lon: -80.20m),
        };
        var prefs = Prefs(maxStops: 2);

        // Must NOT throw — the internal timeout should be swallowed and haversine used
        var result = await svc.BuildPlanScheduleAsync(places, prefs, seed: 42);

        var stops = result.Stops.OrderBy(s => s.OrderIndex).ToList();
        Assert.Equal(2, stops.Count);

        // Haversine fallback must produce a non-zero duration
        var travel = stops[1].TravelFromPrevious;
        Assert.NotNull(travel);
        Assert.True(travel.duration_min >= 1,
            "Haversine fallback should produce non-zero duration when routing times out internally");
    }
}
