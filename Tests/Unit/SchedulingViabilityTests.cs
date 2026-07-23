using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.Routing;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalList.API.Tests.Unit;

/// <summary>
/// Viability battery for the day-aware scheduler (API-2). Every test guards the promise
/// the product presumes to beat ChatGPT on: a generated plan must never send a traveller to
/// a place that is CLOSED on that weekday (C1), nor schedule a visit that runs PAST closing
/// time (M2). Also covers the hard day cap (M3), dead-gap skipping (M4), the per-leg travel
/// ceiling (m8), and the legacy day-agnostic fallback when no trip date is sent.
/// </summary>
public class SchedulingViabilityTests
{
    // Concrete anchor dates (verified weekdays). Google day int == (int)DayOfWeek (0=Sun).
    private static readonly DateOnly Monday  = new(2026, 7, 27); // DayOfWeek.Monday  (1)
    private static readonly DateOnly Tuesday = new(2026, 7, 28); // DayOfWeek.Tuesday (2)

    private static SchedulingService Svc() => new(NullLogger<SchedulingService>.Instance);

    // ── C1: day-of-week awareness (the Vizcaya bug) ───────────────────────────

    [Fact]
    public void C1_ClosedOnStartWeekday_PlaceSkipped()
    {
        // Vizcaya case: place open ONLY on Tuesday. A Monday trip must NOT schedule it
        // by borrowing a different weekday's hours.
        var place  = MakePlace("culture", DayHours(2, 10, 18));
        var result = Svc().BuildPlanSchedule([place], Prefs(startDate: Monday), seed: 1);

        Assert.DoesNotContain(result.Stops, s => s.PlaceId == place.Id);
        Assert.Contains("place_closed_skipped", result.Warnings);
    }

    [Fact]
    public void C1_OpenOnStartWeekday_PlaceScheduled()
    {
        // Same place, Tuesday trip → open that weekday → scheduled and viable.
        var place  = MakePlace("culture", DayHours(2, 10, 18));
        var result = Svc().BuildPlanSchedule([place], Prefs(startDate: Tuesday), seed: 1);

        Assert.Contains(result.Stops, s => s.PlaceId == place.Id);
        Assert.DoesNotContain("place_closed_skipped", result.Warnings);
        AssertViable(result, [place], Tuesday);
    }

    [Fact]
    public void C1_SecondDayMapsToStartDatePlusOne()
    {
        // Two Tuesday-only places, Monday start, 2 days (1 stop/day, round-robin):
        // day 1 (Monday) closed → skipped; day 2 (Tuesday) open → scheduled.
        var p1 = MakePlace("culture", DayHours(2, 10, 18));
        var p2 = MakePlace("culture", DayHours(2, 10, 18));
        var result = Svc().BuildPlanSchedule([p1, p2], Prefs(days: 2, maxStops: 1, startDate: Monday), seed: 1);

        Assert.DoesNotContain(result.Stops, s => s.DayNumber == 1);
        Assert.Contains(result.Stops, s => s.DayNumber == 2);
        AssertViable(result, [p1, p2], Monday);
    }

    // ── M2: visit must fit before closing ─────────────────────────────────────

    [Fact]
    public void M2_VisitRunsPastClose_NotScheduled()
    {
        // Open Monday 16:30–17:00 (30-min window). A 90-min food visit cannot fit before
        // close → must NOT be scheduled (would leave the traveller inside a closed place).
        var narrow = new OpeningHoursData(
            Periods: [new OpeningPeriod(new OpeningTime(1, 16, 30), new OpeningTime(1, 17, 0))],
            WeekdayDescriptions: []);
        var place  = MakePlace("food", narrow, durationMin: 90);
        var result = Svc().BuildPlanSchedule([place], Prefs(startDate: Monday), seed: 1);

        Assert.DoesNotContain(result.Stops, s => s.PlaceId == place.Id);
        Assert.Contains("place_closed_skipped", result.Warnings);
    }

    // ── M3: hard day cap (no clamp-to-23:59) ──────────────────────────────────

    [Fact]
    public void M3_HardCap_TruncatesDay_NoArrivalPastCap_NoDuplicate2359()
    {
        // 10 hours-less culture stops (120 min each) cannot all fit in one day. The old
        // code clamped every overflow arrival to "23:59" (several duplicates). Now the day
        // is truncated: fewer stops, none past 23:00, and no "23:59" duplicates.
        var places = Enumerable.Range(0, 10).Select(_ => MakePlace("culture")).ToList();
        var result = Svc().BuildPlanSchedule(places, Prefs(maxStops: 10), seed: 1);

        Assert.Contains("day_truncated", result.Warnings);
        Assert.All(result.Stops, s =>
            Assert.True(TimeSpan.ParseExact(s.SuggestedArrival!, @"hh\:mm", null) <= new TimeSpan(23, 0, 0),
                $"arrival {s.SuggestedArrival} exceeds the 23:00 hard cap"));
        Assert.Empty(result.Stops.Where(s => s.SuggestedArrival == "23:59"));
        Assert.True(result.Stops.Count < places.Count,
            $"expected truncation to drop candidates, but emitted {result.Stops.Count}/{places.Count}");
        AssertViable(result, places, startDate: null);
    }

    // ── M4: dead-gap skipping (clock does not jump) ───────────────────────────

    [Fact]
    public void M4_DeadGap_SkipsWithoutAdvancingClock()
    {
        // Place A opens only 14:00–18:00 Monday — a 4.5h wait from 09:30, past the 90-min
        // dead-gap tolerance → skipped, and the clock is NOT advanced, so the open place B
        // still schedules early in the day.
        var placeA = MakePlace("culture", DayHours(1, 14, 18));
        var placeB = MakePlace("coffee"); // no hours → open, not day-gated
        var result = Svc().BuildPlanSchedule([placeA, placeB], Prefs(maxStops: 2, startDate: Monday), seed: 1);

        Assert.DoesNotContain(result.Stops, s => s.PlaceId == placeA.Id);
        Assert.Contains("dead_gap_skipped", result.Warnings);

        var bStop = Assert.Single(result.Stops.Where(s => s.PlaceId == placeB.Id));
        Assert.True(TimeSpan.ParseExact(bStop.SuggestedArrival!, @"hh\:mm", null) < new TimeSpan(12, 0, 0),
            "clock must not have jumped forward to A's late opening");
    }

    [Fact]
    public void M4_OpensSoon_ShiftsWithinTolerance_NotSkipped()
    {
        // Opens 10:30 Monday — 60 min after the 09:30 start, within the 90-min tolerance →
        // wait and schedule, not skip.
        var opensAt1030 = new OpeningHoursData(
            Periods: [new OpeningPeriod(new OpeningTime(1, 10, 30), new OpeningTime(1, 18, 0))],
            WeekdayDescriptions: []);
        var place  = MakePlace("coffee", opensAt1030);
        var result = Svc().BuildPlanSchedule([place], Prefs(startDate: Monday), seed: 1);

        var stop = Assert.Single(result.Stops);
        Assert.Equal("10:30", stop.SuggestedArrival);
        Assert.DoesNotContain("dead_gap_skipped", result.Warnings);
        AssertViable(result, [place], Monday);
    }

    // ── m8: per-leg travel ceiling ────────────────────────────────────────────

    [Fact]
    public async Task M8_LegTooFar_SkipsCandidate_AndReResolvesNextLeg()
    {
        // Resolver reports every leg as 70 min > the 60-min cap → every non-first stop is
        // skipped. The 3rd stop re-resolves its leg from the surviving 1st stop (non-consecutive
        // pair → live resolver call), and is skipped too.
        var resolver = new FixedResolver(durationSeconds: 70 * 60, distanceMeters: 50_000);
        var svc = new SchedulingService(NullLogger<SchedulingService>.Instance, resolver);

        var places = new List<Place>
        {
            MakePlace("food",    lat: 25.70m, lon: -80.10m),
            MakePlace("culture", lat: 26.20m, lon: -80.60m),
            MakePlace("coffee",  lat: 25.72m, lon: -80.12m),
        };
        var result = await svc.BuildPlanScheduleAsync(places, Prefs(maxStops: 3), seed: 1);

        Assert.Single(result.Stops); // only the first survives; the other legs exceed the cap
        Assert.Contains("leg_too_far", result.Warnings);
    }

    // ── Legacy fallback (StartDate == null) ───────────────────────────────────

    [Fact]
    public void Legacy_NullStartDate_UsesDayAgnosticGate_NoRegression()
    {
        // No trip date → the day-agnostic gate accepts a matching window on ANY weekday, so a
        // Tuesday-only place IS scheduled (old behavior, unchanged). Day-aware gating only
        // kicks in once the client sends a date.
        var place  = MakePlace("culture", DayHours(2, 10, 18));
        var result = Svc().BuildPlanSchedule([place], Prefs(startDate: null), seed: 1);

        Assert.Contains(result.Stops, s => s.PlaceId == place.Id);
        AssertViable(result, [place], startDate: null);
    }

    // ── Determinism + viability together ──────────────────────────────────────

    [Fact]
    public void Determinism_SameSeedAndDate_SamePlanAndViable()
    {
        var places = Enumerable.Range(0, 12)
            .Select(i => MakePlace(i % 3 == 0 ? "food" : i % 3 == 1 ? "culture" : "coffee",
                AlwaysOpen(), lat: 25.70m + i * 0.01m, lon: -80.10m + i * 0.01m))
            .ToList();
        var prefs = Prefs(days: 2, maxStops: 4, startDate: Monday);

        var r1 = Svc().BuildPlanSchedule(places, prefs, seed: 999);
        var r2 = Svc().BuildPlanSchedule(places, prefs, seed: 999);

        Assert.Equal(r1.Stops.Count, r2.Stops.Count);
        for (int i = 0; i < r1.Stops.Count; i++)
        {
            Assert.Equal(r1.Stops[i].PlaceId,          r2.Stops[i].PlaceId);
            Assert.Equal(r1.Stops[i].DayNumber,        r2.Stops[i].DayNumber);
            Assert.Equal(r1.Stops[i].SuggestedArrival, r2.Stops[i].SuggestedArrival);
        }

        AssertViable(r1, places, Monday);
    }

    // ── AssertViable: the shared invariant checker ────────────────────────────

    /// <summary>
    /// Asserts every emitted stop is viable: within the hard cap, strictly increasing arrivals
    /// per day, and — when the trip date and the place's hours are both known — open on the
    /// correct weekday with the full visit fitting before close.
    /// </summary>
    private static void AssertViable(ScheduleResult result, IEnumerable<Place> places, DateOnly? startDate)
    {
        var byId = places.ToDictionary(p => p.Id);

        foreach (var day in result.Stops.GroupBy(s => s.DayNumber))
        {
            TimeSpan? prev = null;
            foreach (var stop in day.OrderBy(s => s.OrderIndex))
            {
                Assert.True(byId.TryGetValue(stop.PlaceId, out var place), "stop references an unknown place");
                var arrival = TimeSpan.ParseExact(stop.SuggestedArrival!, @"hh\:mm", null);

                var cap = place!.Category.Equals("nightlife", StringComparison.OrdinalIgnoreCase)
                    ? new TimeSpan(23, 59, 0)
                    : new TimeSpan(23, 0, 0);
                Assert.True(arrival <= cap, $"arrival {arrival} exceeds hard cap {cap} for '{place.Category}'");

                if (prev is TimeSpan p)
                    Assert.True(arrival > p, $"arrivals not strictly increasing on day {stop.DayNumber}: {p} then {arrival}");
                prev = arrival;

                if (startDate is DateOnly sd && place.OpeningHours is not null)
                {
                    var data = OpeningHoursData.FromJsonDocument(place.OpeningHours);
                    Assert.NotNull(data);
                    var weekday = sd.AddDays(stop.DayNumber - 1).DayOfWeek;

                    Assert.True(data!.IsOpenAt(weekday, arrival),
                        $"stop at {arrival} on {weekday} is NOT within an opening window");
                    // The full visit must fit before close: NextFitAt at this arrival returns it unchanged.
                    Assert.Equal(arrival, data.NextFitAt(weekday, arrival, stop.SuggestedDurationMin));
                }
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Place MakePlace(
        string category,
        OpeningHoursData? hours = null,
        decimal lat = 25.77m,
        decimal lon = -80.19m,
        int? durationMin = null) => new()
    {
        Id               = Guid.NewGuid(),
        Name             = $"{category}-{Guid.NewGuid().ToString("N")[..6]}",
        Category         = category,
        City             = "Miami",
        Status           = "published",
        WhyThisPlace     = "test",
        Latitude         = lat,
        Longitude        = lon,
        VisitDurationMin = durationMin,
        OpeningHours     = hours?.ToJsonDocument(),
    };

    private static ExtractedPreferences Prefs(
        int days = 1,
        int maxStops = 5,
        string groupType = "couple",
        DateOnly? startDate = null) => new()
    {
        Days           = days,
        MaxStopsPerDay = maxStops,
        GroupType      = groupType,
        Categories     = ["food"],
        StartDate      = startDate,
    };

    // Single window on a specific weekday (Google day int: 0=Sun…6=Sat).
    private static OpeningHoursData DayHours(int day, int openHour, int closeHour) =>
        new(
            Periods: [new OpeningPeriod(new OpeningTime(day, openHour, 0), new OpeningTime(day, closeHour, 0))],
            WeekdayDescriptions: []);

    // Open 24h every day (null Close).
    private static OpeningHoursData AlwaysOpen() =>
        new(
            Periods: [new OpeningPeriod(new OpeningTime(0, 0, 0), null)],
            WeekdayDescriptions: []);

    private sealed class FixedResolver(int durationSeconds, int distanceMeters) : ISegmentResolver
    {
        public Task<RouteSegment?> ResolveSegmentAsync(Place from, Place to, RoutingMode mode, CancellationToken ct)
            => Task.FromResult<RouteSegment?>(new RouteSegment("_poly_", distanceMeters, durationSeconds));

        public Task<List<PlanRouteSegmentDto>> ResolveAsync(ICollection<PlanStop> stops, RoutingMode mode, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
