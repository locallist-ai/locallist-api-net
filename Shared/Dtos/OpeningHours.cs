using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalList.API.NET.Shared.Dtos;

public sealed record OpeningHoursData(
    List<OpeningPeriod> Periods,
    List<string> WeekdayDescriptions
)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public JsonDocument ToJsonDocument() =>
        JsonSerializer.SerializeToDocument(this, JsonOpts);

    public static OpeningHoursData? FromJsonDocument(JsonDocument? doc)
    {
        if (doc is null) return null;
        try
        {
            return JsonSerializer.Deserialize<OpeningHoursData>(
                doc.RootElement.GetRawText(), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static readonly TimeSpan OneDay = TimeSpan.FromHours(24);

    // ── Day-aware API (viability C1: honor period.Open.Day) ────────────────────
    // Google Places (New) regularOpeningHours.periods[].open.day uses
    // 0=Sunday…6=Saturday, which matches (int)System.DayOfWeek 1:1 — no mapping.

    /// <summary>
    /// Returns true if the place is open at <paramref name="clock"/> on the given
    /// calendar <paramref name="day"/> (day-of-week aware). Considers same-day
    /// windows plus the after-midnight tail of the previous day's cross-midnight
    /// windows (arrival Saturday 01:00 matches a Friday 22:00→02:00 window).
    /// </summary>
    public bool IsOpenAt(DayOfWeek day, TimeSpan clock) =>
        FindWindowAt(day, clock) is not null;

    /// <summary>
    /// Returns the start of the next open window at or after <paramref name="clock"/>
    /// on the SAME calendar <paramref name="day"/>, or null if the place has no later
    /// window that day. Never jumps to another day (root cause of the M4 dead-gap bug).
    /// </summary>
    public TimeSpan? NextOpenAt(DayOfWeek day, TimeSpan clock)
    {
        int dayInt = (int)day;
        TimeSpan? best = null;

        foreach (var period in Periods)
        {
            if (period.Open is null) continue;
            if (period.Close is null)
            {
                // 24/7 open — open now, nothing to wait for.
                if (best is null || clock < best) best = clock;
                continue;
            }
            if (period.Open.Day != dayInt) continue; // same calendar day only

            var start = ToTimeSpan(period.Open);
            if (start >= clock && (best is null || start < best)) best = start;
        }

        return best;
    }

    /// <summary>
    /// Returns the earliest arrival time at or after <paramref name="clock"/> on the
    /// given calendar <paramref name="day"/> such that a visit of
    /// <paramref name="durationMin"/> minutes fits entirely within an opening window of
    /// that day (start + duration ≤ close). Returns null when no window of that day can
    /// accommodate the full duration. Only considers same-day windows (does not cross to
    /// another calendar day). Primitive used by the day-clock scheduler (API-2, M2+M4).
    /// </summary>
    public TimeSpan? NextFitAt(DayOfWeek day, TimeSpan clock, int durationMin)
    {
        int dayInt = (int)day;
        var duration = TimeSpan.FromMinutes(durationMin);
        TimeSpan? best = null;

        foreach (var period in Periods)
        {
            if (period.Open is null) continue;
            if (period.Close is null)
            {
                // 24/7 open — fits from the clock, no close constraint.
                if (best is null || clock < best) best = clock;
                continue;
            }
            if (period.Open.Day != dayInt) continue; // same calendar day only

            var start = ToTimeSpan(period.Open);
            var end   = ToTimeSpan(period.Close);
            if (end <= start) end += OneDay; // crosses midnight

            var effectiveStart = clock > start ? clock : start;
            if (effectiveStart + duration <= end && (best is null || effectiveStart < best))
                best = effectiveStart;
        }

        return best;
    }

    private OpeningPeriod? FindWindowAt(DayOfWeek day, TimeSpan clock)
    {
        int dayInt     = (int)day;
        int prevDayInt = ((int)day + 6) % 7; // yesterday, wrapping Sunday→Saturday

        foreach (var period in Periods)
        {
            if (period.Open is null) continue;
            if (period.Close is null) return period; // 24/7 open — always

            var start = ToTimeSpan(period.Open);
            var end   = ToTimeSpan(period.Close);
            bool crossesMidnight = end <= start;
            if (crossesMidnight) end += OneDay;

            if (period.Open.Day == dayInt)
            {
                // Same-day window. For a cross-midnight window the evening slice
                // [start, 24:00) lives here; the post-midnight tail is attributed
                // to the following day via the prevDay branch below.
                if (clock >= start && clock < end) return period;
            }
            else if (crossesMidnight && period.Open.Day == prevDayInt)
            {
                // Previous day's window that spills past midnight into `day`:
                // its tail covers [00:00, end-24:00) of this calendar day.
                if (clock < end - OneDay) return period;
            }
        }

        return null;
    }

    // ── Legacy day-agnostic API ("any day") ────────────────────────────────────
    // Retained for the StartDate==null fallback (client hasn't sent a trip date) and
    // for existing scheduler/test call sites. Accepts a matching period on ANY weekday;
    // the day-aware overloads above should be preferred once a date is known.

    /// <summary>
    /// Returns true if the place is open at <paramref name="timeOfDay"/> on any day of the week.
    /// Fallback used when the calendar date is unknown; accepts any matching period.
    /// </summary>
    public bool IsOpenAt(TimeSpan timeOfDay) =>
        FindWindowAt(timeOfDay) is not null;

    /// <summary>
    /// Returns the start of the next open window at or after <paramref name="timeOfDay"/>,
    /// or null if no window exists within the same "day" (i.e. the place never opens later).
    /// </summary>
    public TimeSpan? NextOpenAt(TimeSpan timeOfDay)
    {
        // Try to find a period that starts after timeOfDay (same day, any weekday).
        var candidates = Periods
            .Where(p => p.Open is not null)
            .Select(p => TimeSpan.FromMinutes(p.Open!.Hour * 60 + p.Open.Minute))
            .Where(t => t > timeOfDay)
            .OrderBy(t => t)
            .ToList();

        return candidates.Count > 0 ? candidates[0] : null;
    }

    private OpeningPeriod? FindWindowAt(TimeSpan timeOfDay)
    {
        double h = timeOfDay.TotalHours;

        foreach (var period in Periods)
        {
            if (period.Open is null) continue;

            double openH = period.Open.Hour + period.Open.Minute / 60.0;

            if (period.Close is null)
            {
                // null Close = open 24h — always fits
                return period;
            }

            double closeH = period.Close.Hour + period.Close.Minute / 60.0;

            if (closeH <= openH)
            {
                // Crosses midnight: open until closeH next day
                if (h >= openH || h < closeH) return period;
            }
            else
            {
                if (h >= openH && h < closeH) return period;
            }
        }

        return null;
    }

    private static TimeSpan ToTimeSpan(OpeningTime t) => new(t.Hour, t.Minute, 0);
}

public sealed record OpeningPeriod(OpeningTime? Open, OpeningTime? Close);

public sealed record OpeningTime(int Day, int Hour, int Minute);
