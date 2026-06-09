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

    /// <summary>
    /// Returns true if the place is open at <paramref name="timeOfDay"/> on any day of the week.
    /// Phase-1 limitation: we don't know the actual calendar date, so we accept any matching period.
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
}

public sealed record OpeningPeriod(OpeningTime? Open, OpeningTime? Close);

public sealed record OpeningTime(int Day, int Hour, int Minute);
