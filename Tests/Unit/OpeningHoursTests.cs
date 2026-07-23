using System.Text.Json;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalList.API.Tests.Unit;

public class OpeningHoursTests
{
    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ToAndFromJsonDocument_PreservesData()
    {
        var data = new OpeningHoursData(
            Periods:
            [
                new OpeningPeriod(new OpeningTime(1, 9, 0), new OpeningTime(1, 22, 0)),
                new OpeningPeriod(new OpeningTime(0, 0, 0), null),
            ],
            WeekdayDescriptions: ["Monday: 9:00 AM – 10:00 PM", "Sunday: Open 24 hours"]);

        var doc    = data.ToJsonDocument();
        var result = OpeningHoursData.FromJsonDocument(doc);

        Assert.NotNull(result);
        Assert.Equal(2, result.Periods.Count);
        Assert.Equal(1,  result.Periods[0].Open!.Day);
        Assert.Equal(9,  result.Periods[0].Open!.Hour);
        Assert.Equal(22, result.Periods[0].Close!.Hour);
        Assert.Null(result.Periods[1].Close);
        Assert.Equal(2, result.WeekdayDescriptions.Count);
    }

    [Fact]
    public void FromJsonDocument_Null_ReturnsNull() =>
        Assert.Null(OpeningHoursData.FromJsonDocument(null));

    [Fact]
    public void FromJsonDocument_MalformedJson_ReturnsNull()
    {
        var malformed = JsonDocument.Parse("{\"periods\":\"not-an-array\"}");
        Assert.Null(OpeningHoursData.FromJsonDocument(malformed));
    }

    // ── IsOpenAt ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsOpenAt_WithinPeriod_ReturnsTrue() =>
        Assert.True(SimpleHours(9, 22).IsOpenAt(TimeSpan.FromHours(12)));

    [Fact]
    public void IsOpenAt_BeforeOpen_ReturnsFalse() =>
        Assert.False(SimpleHours(9, 22).IsOpenAt(TimeSpan.FromHours(8)));

    [Fact]
    public void IsOpenAt_AfterClose_ReturnsFalse() =>
        Assert.False(SimpleHours(9, 22).IsOpenAt(TimeSpan.FromHours(22.5)));

    [Fact]
    public void IsOpenAt_MidnightCross_BeforeMidnight_ReturnsTrue()
    {
        var data = new OpeningHoursData(
            Periods: [new OpeningPeriod(new OpeningTime(5, 22, 0), new OpeningTime(6, 2, 0))],
            WeekdayDescriptions: []);
        Assert.True(data.IsOpenAt(TimeSpan.FromHours(23)));
    }

    [Fact]
    public void IsOpenAt_MidnightCross_AfterMidnight_ReturnsTrue()
    {
        var data = new OpeningHoursData(
            Periods: [new OpeningPeriod(new OpeningTime(5, 22, 0), new OpeningTime(6, 2, 0))],
            WeekdayDescriptions: []);
        Assert.True(data.IsOpenAt(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void IsOpenAt_NullClose_AlwaysOpen()
    {
        var data = new OpeningHoursData(
            Periods: [new OpeningPeriod(new OpeningTime(0, 0, 0), null)],
            WeekdayDescriptions: []);
        Assert.True(data.IsOpenAt(TimeSpan.FromHours(3)));
        Assert.True(data.IsOpenAt(TimeSpan.FromHours(14)));
    }

    // ── NextOpenAt ────────────────────────────────────────────────────────────

    [Fact]
    public void NextOpenAt_ClockBeforeOpen_ReturnsOpenTime()
    {
        var next = SimpleHours(14, 22).NextOpenAt(TimeSpan.FromHours(9));
        Assert.Equal(TimeSpan.FromHours(14), next);
    }

    [Fact]
    public void NextOpenAt_NoLaterPeriod_ReturnsNull()
    {
        var next = SimpleHours(9, 17).NextOpenAt(TimeSpan.FromHours(20));
        Assert.Null(next);
    }

    // ── Day-aware IsOpenAt (viability C1) ──────────────────────────────────────
    // Google Places open.day: 0=Sunday…6=Saturday == (int)DayOfWeek.

    [Fact]
    public void IsOpenAt_DayAware_MatchesSameDay()
    {
        // Tuesday(2) 10:00–18:00
        var data = DayHours(2, 10, 18);
        Assert.True(data.IsOpenAt(DayOfWeek.Tuesday, TimeSpan.FromHours(12)));
    }

    [Fact]
    public void IsOpenAt_DayAware_MondayClosed_DoesNotMatchTuesdayHours()
    {
        // Vizcaya case: only open Tuesday(2); arriving on Monday must NOT match.
        var data = DayHours(2, 10, 18);
        Assert.False(data.IsOpenAt(DayOfWeek.Monday, TimeSpan.FromHours(12)));
        Assert.True(data.IsOpenAt(DayOfWeek.Tuesday, TimeSpan.FromHours(12)));
    }

    [Fact]
    public void IsOpenAt_DayAware_BeforeOpen_And_AfterClose_False()
    {
        var data = DayHours(2, 10, 18);
        Assert.False(data.IsOpenAt(DayOfWeek.Tuesday, TimeSpan.FromHours(9)));
        Assert.False(data.IsOpenAt(DayOfWeek.Tuesday, TimeSpan.FromHours(18)));
    }

    [Fact]
    public void IsOpenAt_DayAware_CrossMidnight_EveningOnOpenDay()
    {
        // Friday(5) 22:00 → Saturday(6) 02:00
        var data = CrossMidnight(5, 22, 6, 2);
        Assert.True(data.IsOpenAt(DayOfWeek.Friday, TimeSpan.FromHours(23)));
    }

    [Fact]
    public void IsOpenAt_DayAware_CrossMidnight_TailAttributedToNextDay()
    {
        // Friday 22:00 → Saturday 02:00: arrival Saturday 01:00 matches the tail.
        var data = CrossMidnight(5, 22, 6, 2);
        Assert.True(data.IsOpenAt(DayOfWeek.Saturday, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void IsOpenAt_DayAware_CrossMidnight_DoesNotBleedToOtherDays()
    {
        var data = CrossMidnight(5, 22, 6, 2);
        // Sunday 01:00 is NOT covered (only Friday's tail spills into Saturday).
        Assert.False(data.IsOpenAt(DayOfWeek.Sunday, TimeSpan.FromHours(1)));
        // Saturday evening 22:00 is NOT covered (no Saturday period).
        Assert.False(data.IsOpenAt(DayOfWeek.Saturday, TimeSpan.FromHours(22)));
    }

    [Fact]
    public void IsOpenAt_DayAware_NullClose_OpenEveryDay()
    {
        var data = new OpeningHoursData(
            Periods: [new OpeningPeriod(new OpeningTime(0, 0, 0), null)],
            WeekdayDescriptions: []);
        Assert.True(data.IsOpenAt(DayOfWeek.Wednesday, TimeSpan.FromHours(3)));
        Assert.True(data.IsOpenAt(DayOfWeek.Sunday, TimeSpan.FromHours(23)));
    }

    // ── Day-aware NextOpenAt (M4: never jump to another day) ───────────────────

    [Fact]
    public void NextOpenAt_DayAware_SameDayLaterWindow_ReturnsIt()
    {
        // Monday(1) 14:00–18:00 + Tuesday(2) 09:00–12:00.
        var data = new OpeningHoursData(
            Periods:
            [
                new OpeningPeriod(new OpeningTime(1, 14, 0), new OpeningTime(1, 18, 0)),
                new OpeningPeriod(new OpeningTime(2, 9, 0),  new OpeningTime(2, 12, 0)),
            ],
            WeekdayDescriptions: []);
        // Clock 09:00 Monday → must return Monday 14:00, NOT Tuesday 09:00.
        Assert.Equal(TimeSpan.FromHours(14), data.NextOpenAt(DayOfWeek.Monday, TimeSpan.FromHours(9)));
    }

    [Fact]
    public void NextOpenAt_DayAware_NoSameDayWindow_ReturnsNull()
    {
        // Only Tuesday hours; asking on Monday must NOT jump to Tuesday.
        var data = DayHours(2, 9, 12);
        Assert.Null(data.NextOpenAt(DayOfWeek.Monday, TimeSpan.FromHours(9)));
    }

    // ── NextFitAt (duration must fit; same calendar day only) ──────────────────

    [Fact]
    public void NextFitAt_ClockBeforeOpen_ReturnsOpenWhenDurationFits()
    {
        var data = DayHours(1, 14, 18); // Monday 14–18
        Assert.Equal(TimeSpan.FromHours(14), data.NextFitAt(DayOfWeek.Monday, TimeSpan.FromHours(9), 60));
    }

    [Fact]
    public void NextFitAt_ClockInsideWindow_ReturnsClock()
    {
        var data = DayHours(1, 9, 17);
        Assert.Equal(TimeSpan.FromHours(15), data.NextFitAt(DayOfWeek.Monday, TimeSpan.FromHours(15), 60));
    }

    [Fact]
    public void NextFitAt_NotEnoughTimeBeforeClose_ReturnsNull()
    {
        var data = DayHours(1, 9, 17); // closes 17:00
        // 16:30 + 60min = 17:30 > 17:00 → does not fit, and no later window that day.
        Assert.Null(data.NextFitAt(DayOfWeek.Monday, new TimeSpan(16, 30, 0), 60));
    }

    [Fact]
    public void NextFitAt_DoesNotCrossToAnotherDay()
    {
        // Monday 9–17 (won't fit a 120min visit at 16:30) + Tuesday 9–12.
        var data = new OpeningHoursData(
            Periods:
            [
                new OpeningPeriod(new OpeningTime(1, 9, 0),  new OpeningTime(1, 17, 0)),
                new OpeningPeriod(new OpeningTime(2, 9, 0),  new OpeningTime(2, 12, 0)),
            ],
            WeekdayDescriptions: []);
        // Monday can't fit; must NOT borrow Tuesday's window → null.
        Assert.Null(data.NextFitAt(DayOfWeek.Monday, new TimeSpan(16, 30, 0), 120));
    }

    [Fact]
    public void NextFitAt_MultipleWindows_ReturnsEarliestThatFits()
    {
        // Monday 9–12 and Monday 14–18. Clock 11:30, 60min: first window fails
        // (11:30+60=12:30>12), second fits → 14:00.
        var data = new OpeningHoursData(
            Periods:
            [
                new OpeningPeriod(new OpeningTime(1, 9, 0),  new OpeningTime(1, 12, 0)),
                new OpeningPeriod(new OpeningTime(1, 14, 0), new OpeningTime(1, 18, 0)),
            ],
            WeekdayDescriptions: []);
        Assert.Equal(TimeSpan.FromHours(14), data.NextFitAt(DayOfWeek.Monday, new TimeSpan(11, 30, 0), 60));
    }

    // ── Scheduler integration ─────────────────────────────────────────────────

    [Fact]
    public void Scheduler_NullOpeningHours_DegradesFine()
    {
        var svc    = Svc();
        var places = new List<Place> { MakePlace("Coffee") };
        var result = svc.BuildPlanSchedule(places, DefaultPrefs(days: 1), seed: 1);
        Assert.NotEmpty(result.Stops);
        Assert.DoesNotContain("place_closed_skipped", result.Warnings);
    }

    [Fact]
    public void Scheduler_PlaceOpenAtClock_NoShift()
    {
        var svc    = Svc();
        var hours  = SimpleHours(9, 22);
        var places = new List<Place> { MakePlace("Coffee", openingHours: hours) };
        var result = svc.BuildPlanSchedule(places, DefaultPrefs(days: 1), seed: 1);

        Assert.NotEmpty(result.Stops);
        Assert.DoesNotContain("place_closed_skipped", result.Warnings);
    }

    [Fact]
    public void Scheduler_PlaceClosedAtClock_ShiftsForward()
    {
        var svc    = Svc();
        // Only opens at 14:00; planner clock starts at 09:30 → must shift forward
        var hours  = SimpleHours(14, 22);
        var places = new List<Place> { MakePlace("Nightlife", openingHours: hours) };
        var result = svc.BuildPlanSchedule(places, DefaultPrefs(days: 1), seed: 1);

        Assert.NotEmpty(result.Stops);
        var arrival = TimeSpan.Parse(result.Stops.First().SuggestedArrival!);
        Assert.True(arrival >= TimeSpan.FromHours(14),
            $"Expected arrival >= 14:00 but was {result.Stops.First().SuggestedArrival}");
        Assert.DoesNotContain("place_closed_skipped", result.Warnings);
    }

    [Fact]
    public void Scheduler_PlaceNeverOpenDuringDay_IsSkipped()
    {
        var svc   = Svc();
        // Closed bar open only 02:00–04:00; open cafe with no restrictions
        var closedHours = new OpeningHoursData(
            Periods: [new OpeningPeriod(new OpeningTime(1, 2, 0), new OpeningTime(1, 4, 0))],
            WeekdayDescriptions: []);

        var nightBar = MakePlace("Nightlife", openingHours: closedHours);
        var openCafe = MakePlace("Coffee");
        var result   = Svc().BuildPlanSchedule(
            [nightBar, openCafe], DefaultPrefs(days: 1, maxStops: 3), seed: 1);

        Assert.Contains("place_closed_skipped", result.Warnings);
        Assert.DoesNotContain(result.Stops, s => s.PlaceId == nightBar.Id);
        Assert.Contains(result.Stops, s => s.PlaceId == openCafe.Id);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SchedulingService Svc() =>
        new(NullLogger<SchedulingService>.Instance);

    private static Place MakePlace(string category, OpeningHoursData? openingHours = null) =>
        new()
        {
            Id           = Guid.NewGuid(),
            Name         = $"{category}-test",
            Category     = category,
            City         = "Miami",
            Status       = "published",
            WhyThisPlace = "test",
            Latitude     = 25.77m,
            Longitude    = -80.19m,
            OpeningHours = openingHours?.ToJsonDocument(),
        };

    private static ExtractedPreferences DefaultPrefs(int days = 1, int maxStops = 5) =>
        new()
        {
            Days          = days,
            MaxStopsPerDay = maxStops,
            GroupType     = "couple",
            Categories    = ["food"],
        };

    private static OpeningHoursData SimpleHours(int openHour, int closeHour) =>
        new(
            Periods: [new OpeningPeriod(new OpeningTime(1, openHour, 0), new OpeningTime(1, closeHour, 0))],
            WeekdayDescriptions: []);

    // Single window on a specific weekday (Google day int: 0=Sun…6=Sat).
    private static OpeningHoursData DayHours(int day, int openHour, int closeHour) =>
        new(
            Periods: [new OpeningPeriod(new OpeningTime(day, openHour, 0), new OpeningTime(day, closeHour, 0))],
            WeekdayDescriptions: []);

    // A single window that crosses midnight (open on openDay, closes on closeDay).
    private static OpeningHoursData CrossMidnight(int openDay, int openHour, int closeDay, int closeHour) =>
        new(
            Periods: [new OpeningPeriod(new OpeningTime(openDay, openHour, 0), new OpeningTime(closeDay, closeHour, 0))],
            WeekdayDescriptions: []);
}
