using LocalList.API.NET.Features.Builder.Shared;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Builder.Services;

public class ScheduledStopDto
{
    public Guid PlaceId { get; set; }
    public int DayNumber { get; set; }
    public int OrderIndex { get; set; }
    public string TimeBlock { get; set; } = string.Empty;
    public string? SuggestedArrival { get; set; }
    public int SuggestedDurationMin { get; set; }
    public TravelInfoDto? TravelFromPrevious { get; set; }
}

public class TravelInfoDto
{
    public double distance_km { get; set; }
    public int duration_min { get; set; }
    public string mode { get; set; } = "drive";
}

public sealed class ScheduleResult
{
    public List<ScheduledStopDto> Stops { get; } = new();
    public List<string> Warnings { get; } = new();
}

public class SchedulingService
{
    private readonly ILogger<SchedulingService> _logger;

    public SchedulingService(ILogger<SchedulingService> logger)
    {
        _logger = logger;
    }

    private static readonly Dictionary<string, HashSet<string>> TimeBlockCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["morning"]   = new(StringComparer.OrdinalIgnoreCase) { "coffee", "wellness", "outdoors", "culture", "food" },
        ["lunch"]     = new(StringComparer.OrdinalIgnoreCase) { "food", "coffee" },
        ["afternoon"] = new(StringComparer.OrdinalIgnoreCase) { "coffee", "outdoors", "culture", "food" },
        ["dinner"]    = new(StringComparer.OrdinalIgnoreCase) { "food" },
        ["evening"]   = new(StringComparer.OrdinalIgnoreCase) { "nightlife", "food", "culture" },
    };

    private static readonly Dictionary<string, string[]> BestTimeMatches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["morning"]   = new[] { "morning" },
        ["lunch"]     = new[] { "lunch", "morning", "afternoon" },
        ["afternoon"] = new[] { "afternoon", "morning" },
        ["dinner"]    = new[] { "dinner", "evening", "lunch" },
        ["evening"]   = new[] { "evening" },
    };

    private static readonly object[] DayTemplate =
    {
        new { TimeBlock = "morning",   Arrival = "09:00", Duration = 60 },
        new { TimeBlock = "lunch",     Arrival = "12:00", Duration = 90 },
        new { TimeBlock = "afternoon", Arrival = "14:30", Duration = 90 },
        new { TimeBlock = "dinner",    Arrival = "19:00", Duration = 90 },
        new { TimeBlock = "evening",   Arrival = "21:00", Duration = 60 },
    };

    public ScheduleResult BuildPlanSchedule(List<Place> filteredPlaces, ExtractedPreferences prefs)
    {
        var result = new ScheduleResult();
        var stops = result.Stops;
        var usedPlaceIds = new HashSet<Guid>();

        var shuffled = filteredPlaces.OrderBy(_ => Random.Shared.Next()).ToList();

        var dayTemplate = new[]
        {
            new { TimeBlock = "morning",   Arrival = "09:00", Duration = 60 },
            new { TimeBlock = "lunch",     Arrival = "12:00", Duration = 90 },
            new { TimeBlock = "afternoon", Arrival = "14:30", Duration = 90 },
            new { TimeBlock = "dinner",    Arrival = "19:00", Duration = 90 },
            new { TimeBlock = "evening",   Arrival = "21:00", Duration = 60 },
        };

        for (int day = 1; day <= prefs.Days; day++)
        {
            var daySlots = dayTemplate.Take(prefs.MaxStopsPerDay).ToList();
            double? prevLat = null;
            double? prevLon = null;

            for (int i = 0; i < daySlots.Count; i++)
            {
                var slot = daySlots[i];

                var strictEligible = shuffled.Where(p =>
                    !usedPlaceIds.Contains(p.Id) &&
                    IsGoodTimeMatch(p, slot.TimeBlock, prefs, strict: true)).ToList();

                var eligibleCount = strictEligible.Count;
                var place = strictEligible.FirstOrDefault();
                if (place == null)
                {
                    var relaxed = shuffled.FirstOrDefault(p =>
                        !usedPlaceIds.Contains(p.Id) &&
                        IsGoodTimeMatch(p, slot.TimeBlock, prefs, strict: false));
                    if (relaxed != null)
                    {
                        _logger.LogWarning(
                            "Builder: schedule soft fallback day={Day} slot={Slot} strictCount=0 relaxedPick={Place}",
                            day, slot.TimeBlock, relaxed.Name);
                        place = relaxed;
                        if (!result.Warnings.Contains("catalog_relaxed_fallback"))
                            result.Warnings.Add("catalog_relaxed_fallback");
                    }
                }
                else
                {
                    _logger.LogInformation(
                        "Builder: schedule day={Day} slot={Slot} strictEligible={Count} pick={Place}",
                        day, slot.TimeBlock, eligibleCount, place.Name);
                }

                if (place == null) continue;

                usedPlaceIds.Add(place.Id);

                TravelInfoDto? travelInfo = null;
                if (prevLat.HasValue && prevLon.HasValue && place.Latitude.HasValue && place.Longitude.HasValue)
                {
                    var dist = Haversine(prevLat.Value, prevLon.Value, (double)place.Latitude.Value, (double)place.Longitude.Value);
                    var mode = dist < 2 ? "walk" : "drive";
                    travelInfo = new TravelInfoDto
                    {
                        distance_km = Math.Round(dist, 1),
                        duration_min = EstimateTravelTime(dist, mode),
                        mode = mode
                    };
                }

                stops.Add(new ScheduledStopDto
                {
                    PlaceId = place.Id,
                    DayNumber = day,
                    OrderIndex = i,
                    TimeBlock = slot.TimeBlock,
                    SuggestedArrival = slot.Arrival,
                    SuggestedDurationMin = slot.Duration,
                    TravelFromPrevious = travelInfo
                });

                if (place.Latitude.HasValue && place.Longitude.HasValue)
                {
                    prevLat = (double)place.Latitude.Value;
                    prevLon = (double)place.Longitude.Value;
                }
            }
        }

        return result;
    }

    internal bool IsGoodTimeMatch(Place place, string timeBlock, ExtractedPreferences prefs, bool strict)
    {
        if (strict)
        {
            if (GroupTypePolicy.IsFamilyContext(prefs.GroupType) &&
                string.Equals(place.Category, "nightlife", StringComparison.OrdinalIgnoreCase))
                return false;

            if (TimeBlockCategories.TryGetValue(timeBlock, out var allowedCategories))
            {
                if (!allowedCategories.Contains(place.Category))
                    return false;
            }
        }

        if (string.IsNullOrEmpty(place.BestTime) || place.BestTime.ToLower() == "any") return true;
        if (BestTimeMatches.TryGetValue(timeBlock, out var matchingTimes))
            return matchingTimes.Any(t => place.BestTime.ToLower().Contains(t));

        return true;
    }

    public object ResolveStopPlaces(List<ScheduledStopDto> stops, List<Place> allPlaces)
    {
        var placeMap = allPlaces.ToDictionary(p => p.Id);

        return stops.Select(stop =>
        {
            placeMap.TryGetValue(stop.PlaceId, out var place);
            return new
            {
                id = Guid.NewGuid(),
                placeId = stop.PlaceId,
                dayNumber = stop.DayNumber,
                orderIndex = stop.OrderIndex,
                timeBlock = stop.TimeBlock,
                suggestedArrival = stop.SuggestedArrival,
                suggestedDurationMin = stop.SuggestedDurationMin,
                travelFromPrevious = stop.TravelFromPrevious,
                place = place != null ? new
                {
                    id = place.Id,
                    name = place.Name,
                    category = place.Category,
                    neighborhood = place.Neighborhood,
                    whyThisPlace = place.WhyThisPlace,
                    priceRange = place.PriceRange,
                    photos = place.Photos,
                    latitude = place.Latitude,
                    longitude = place.Longitude
                } : null
            };
        });
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double ToRad(double degrees) => degrees * (Math.PI / 180);

    private static int EstimateTravelTime(double distanceKm, string mode)
    {
        var speedKmH = mode == "walk" ? 5.0 : 30.0;
        var timeHours = distanceKm / speedKmH;
        return (int)Math.Max(5, Math.Round(timeHours * 60));
    }
}
