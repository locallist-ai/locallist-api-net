using LocalList.API.NET.Features.Builder.Shared;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.Routing;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Builder.Services;

public class SchedulingService
{
    private readonly ILogger<SchedulingService> _logger;
    private readonly ISegmentResolver? _resolver;

    public SchedulingService(ILogger<SchedulingService> logger, ISegmentResolver? resolver = null)
    {
        _logger = logger;
        _resolver = resolver;
    }

    // ── Time-block compatibility (kept for IsGoodTimeMatch) ───────────────────

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

    // ── Visit durations by category ───────────────────────────────────────────

    private static readonly Dictionary<string, int> CategoryDurationMin = new(StringComparer.OrdinalIgnoreCase)
    {
        ["coffee"]        = 45,
        ["food"]          = 90,
        ["culture"]       = 120,
        ["outdoors"]      = 75,
        ["wellness"]      = 90,
        ["nightlife"]     = 120,
        ["shopping"]      = 60,
        ["entertainment"] = 105,
    };
    private const int DefaultDurationMin = 75;

    // ── Day scheduling constants ──────────────────────────────────────────────

    private static readonly TimeSpan DayStart       = TimeSpan.FromHours(9.5);   // 09:30
    private static readonly TimeSpan DaySoftCap     = TimeSpan.FromHours(22);    // 22:00
    private static readonly TimeSpan LunchIdeal     = TimeSpan.FromHours(13);    // 13:00
    private static readonly TimeSpan LunchWinStart  = TimeSpan.FromHours(11.5);  // 11:30
    private static readonly TimeSpan LunchWinEnd    = TimeSpan.FromHours(14.5);  // 14:30
    private static readonly TimeSpan DinnerIdeal    = TimeSpan.FromHours(19.5);  // 19:30
    private static readonly TimeSpan DinnerWinStart = TimeSpan.FromHours(18);    // 18:00
    private static readonly TimeSpan DinnerWinEnd   = TimeSpan.FromHours(21.5);  // 21:30

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Async version — uses <see cref="ISegmentResolver"/> for real travel durations when available.
    /// Same seed guarantees the same candidate ranking and selection logic.
    /// Travel times may vary (Mapbox vs haversine fallback), which can affect
    /// which stops fit within the day's time budget.
    /// </summary>
    public async Task<ScheduleResult> BuildPlanScheduleAsync(
        List<Place> filteredPlaces, ExtractedPreferences prefs, int? seed = null, CancellationToken ct = default)
    {
        var result = new ScheduleResult();
        var rng = new Random(seed ?? Random.Shared.Next());

        var refined = ApplyRefinements(filteredPlaces, prefs, result);
        var effectiveMaxStops = ResolveEffectiveMaxStops(prefs);
        var dayPlaces = SelectPlacesForDays(refined, prefs, effectiveMaxStops, rng);

        for (int day = 1; day <= prefs.Days; day++)
        {
            if (!dayPlaces.TryGetValue(day, out var places) || places.Count == 0) continue;
            await ScheduleDayAsync(places, result, day, rng, ct);
        }

        return result;
    }

    /// <summary>
    /// Sync wrapper for backward compatibility (unit tests). Production code should prefer
    /// <see cref="BuildPlanScheduleAsync"/>.
    /// </summary>
    public ScheduleResult BuildPlanSchedule(
        List<Place> filteredPlaces, ExtractedPreferences prefs, int? seed = null)
        => BuildPlanScheduleAsync(filteredPlaces, prefs, seed).GetAwaiter().GetResult();

    // ── Step 1: pace clamp ────────────────────────────────────────────────────

    internal static int ResolveEffectiveMaxStops(ExtractedPreferences prefs) =>
        prefs.Pace?.ToLowerInvariant() switch
        {
            "slow" => Math.Min(prefs.MaxStopsPerDay, 3),
            "fast" => Math.Max(prefs.MaxStopsPerDay, 5),
            _      => prefs.MaxStopsPerDay
        };

    // ── Step 2: weighted sampling across days ─────────────────────────────────

    // filteredPlaces llega con orden determinista y "mejor primero": pre-ranked desc
    // por PlaceRankingService en la ruta RAG (index 0 = mejor score), u ordenado por
    // Id en el fallback keyword (sin ranking semántico). En ambos casos el gate de
    // categoría antepone la categoría pedida, así que index 0 = mejor disponible.
    private static Dictionary<int, List<Place>> SelectPlacesForDays(
        List<Place> ranked, ExtractedPreferences prefs, int maxStops, Random rng)
    {
        var isFamily = GroupTypePolicy.IsFamilyContext(prefs.GroupType);

        // Family groups never see nightlife
        var eligible = isFamily
            ? ranked.Where(p => !p.Category.Equals("nightlife", StringComparison.OrdinalIgnoreCase)).ToList()
            : ranked;

        int totalSlots = prefs.Days * maxStops;
        int poolSize   = Math.Min(eligible.Count, Math.Max(totalSlots * 2, totalSlots + 5));
        var pool       = eligible.Take(poolSize).ToList();

        int needed = Math.Min(totalSlots, pool.Count);

        // Rank-first: la mitad superior de los slots va directa a los mejor rankeados
        // (determinista); el resto se muestrea ponderado para mantener variedad entre
        // semillas. Antes TODO era sampling y los top del ranking podían quedarse
        // fuera del plan — el usuario pedía X y recibía suplentes.
        int guaranteed = (needed + 1) / 2;
        var selected   = new List<Place>(needed);
        selected.AddRange(pool.Take(guaranteed));

        // Weighted sampling without replacement sobre el resto: weight = poolSize - index
        var availableIdxs = Enumerable.Range(guaranteed, Math.Max(0, pool.Count - guaranteed)).ToList();

        while (selected.Count < needed && availableIdxs.Count > 0)
        {
            double totalWeight = availableIdxs.Select(i => (double)(pool.Count - i)).Sum();
            double pick        = rng.NextDouble() * totalWeight;
            double cumul       = 0;
            int chosenListPos  = availableIdxs.Count - 1;
            for (int i = 0; i < availableIdxs.Count; i++)
            {
                cumul += (double)(pool.Count - availableIdxs[i]);
                if (pick <= cumul) { chosenListPos = i; break; }
            }
            selected.Add(pool[availableIdxs[chosenListPos]]);
            availableIdxs.RemoveAt(chosenListPos);
        }

        // Round-robin distribution across days
        var result = new Dictionary<int, List<Place>>(prefs.Days);
        for (int d = 1; d <= prefs.Days; d++) result[d] = new List<Place>();
        for (int i = 0; i < selected.Count; i++) result[(i % prefs.Days) + 1].Add(selected[i]);

        // Best-effort: ensure ≥1 food per day
        EnsureFoodPerDay(result, eligible, selected, prefs.Days);
        return result;
    }

    private static void EnsureFoodPerDay(
        Dictionary<int, List<Place>> dayPlaces,
        IEnumerable<Place> eligible,
        List<Place> alreadySelected,
        int days)
    {
        var usedIds    = alreadySelected.Select(p => p.Id).ToHashSet();
        var unusedFood = eligible
            .Where(p => p.Category.Equals("food", StringComparison.OrdinalIgnoreCase) && !usedIds.Contains(p.Id))
            .ToList();

        for (int day = 1; day <= days; day++)
        {
            var places = dayPlaces[day];
            if (places.Any(p => p.Category.Equals("food", StringComparison.OrdinalIgnoreCase))) continue;
            if (unusedFood.Count == 0) break;

            var food = unusedFood[0];
            unusedFood.RemoveAt(0);
            usedIds.Add(food.Id);
            if (places.Count > 0)
                places[^1] = food; // replace lowest-ranked (last) stop
            else
                places.Add(food);
        }
    }

    // ── Step 3: schedule one day ──────────────────────────────────────────────

    private async Task ScheduleDayAsync(List<Place> places, ScheduleResult result, int day, Random rng, CancellationToken ct)
    {
        var ordered = OrderByGeography(places, rng);
        AnchorMeals(ordered);       // position food near meal windows first
        AnchorNightlife(ordered);   // then push nightlife to end (always last)

        // Pre-fetch all consecutive travel segments in parallel before the sequential clock walk.
        // Avoids up to N-1 sequential Mapbox calls on cold cache; DB cache writes are idempotent
        // (ON CONFLICT DO NOTHING), so concurrent pre-fetches across requests are safe.
        var prefetched = await PrefetchDaySegmentsAsync(ordered, ct);
        await WalkDayClockAsync(ordered, result, day, prefetched, ct);
    }

    // Pre-fetches route segments for all consecutive place pairs in parallel (concurrency cap 4,
    // matching the batch resolver in RouteResolver.FetchAndPersistAsync).
    // Returns a dict keyed by (fromId, toId); entries that fail resolver fall back to Haversine
    // inside ResolveTravelAsync and are still stored so WalkDayClockAsync gets a dict hit.
    private async Task<Dictionary<(Guid, Guid), TravelInfoDto>> PrefetchDaySegmentsAsync(
        List<Place> ordered, CancellationToken ct)
    {
        if (_resolver == null || ordered.Count < 2) return [];

        var pairs = new List<(Place from, Place to, double dist, string mode)>(ordered.Count - 1);
        for (int i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var curr = ordered[i];
            if (prev.Latitude.HasValue && prev.Longitude.HasValue &&
                curr.Latitude.HasValue && curr.Longitude.HasValue)
            {
                double dist = Haversine(
                    (double)prev.Latitude.Value, (double)prev.Longitude.Value,
                    (double)curr.Latitude.Value, (double)curr.Longitude.Value);
                pairs.Add((prev, curr, dist, dist < 2 ? "walk" : "drive"));
            }
        }

        if (pairs.Count == 0) return [];

        using var semaphore = new SemaphoreSlim(4);
        var tasks = pairs.Select(async pair =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var travel = await ResolveTravelAsync(pair.from, pair.to, pair.dist, pair.mode, ct);
                return (key: (pair.from.Id, pair.to.Id), travel);
            }
            finally { semaphore.Release(); }
        });

        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(r => r.key, r => r.travel);
    }

    // ── Geographic ordering ───────────────────────────────────────────────────

    private static List<Place> OrderByGeography(List<Place> places, Random rng)
    {
        var withCoords    = places.Where(p => p.Latitude.HasValue && p.Longitude.HasValue).ToList();
        var withoutCoords = places.Where(p => !p.Latitude.HasValue || !p.Longitude.HasValue).ToList();

        if (withCoords.Count == 0) return places.ToList();

        // Anchor: sample among top 1-3 by rank (variety across seeds)
        int anchorCandidates = Math.Min(3, withCoords.Count);
        var anchor           = withCoords[rng.Next(anchorCandidates)];

        var ordered   = new List<Place> { anchor };
        var remaining = withCoords.Where(p => p.Id != anchor.Id).ToList();

        // Nearest-neighbor greedy; tie-break by Id for determinism
        while (remaining.Count > 0)
        {
            var last    = ordered[^1];
            double lat1 = (double)last.Latitude!.Value;
            double lon1 = (double)last.Longitude!.Value;

            var nearest = remaining
                .OrderBy(p => Haversine(lat1, lon1, (double)p.Latitude!.Value, (double)p.Longitude!.Value))
                .ThenBy(p => p.Id)
                .First();

            ordered.Add(nearest);
            remaining.Remove(nearest);
        }

        // Places without coords at the end, preserving their relative rank
        ordered.AddRange(withoutCoords.OrderBy(p => places.IndexOf(p)));
        return ordered;
    }

    // ── Nightlife anchoring ───────────────────────────────────────────────────

    private static void AnchorNightlife(List<Place> places)
    {
        var nightlife = places
            .Where(p => p.Category.Equals("nightlife", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (nightlife.Count == 0) return;

        foreach (var nl in nightlife) { places.Remove(nl); places.Add(nl); }
    }

    // ── Meal anchoring ────────────────────────────────────────────────────────

    private static void AnchorMeals(List<Place> places)
    {
        if (places.Count < 2) return;
        TryAnchorFoodToSlot(places, LunchIdeal,  LunchWinStart,  LunchWinEnd);
        TryAnchorFoodToSlot(places, DinnerIdeal, DinnerWinStart, DinnerWinEnd);
    }

    private static void TryAnchorFoodToSlot(
        List<Place> places, TimeSpan ideal, TimeSpan winStart, TimeSpan winEnd)
    {
        var arrivals = SimulateArrivals(places);

        // Only anchor when the day's schedule reaches the target window
        if (!arrivals.Any(a => a >= winStart && a <= winEnd)) return;

        // Target: position with arrival closest to ideal inside or nearest to the window
        int targetPos = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < arrivals.Length; i++)
        {
            double dist = Math.Abs((arrivals[i] - ideal).TotalMinutes);
            if (dist < bestDist) { bestDist = dist; targetPos = i; }
        }

        // Already food at target → nothing to swap
        if (places[targetPos].Category.Equals("food", StringComparison.OrdinalIgnoreCase)) return;

        // Find food place nearest by position distance (not nightlife, already at end)
        int foodIdx  = -1;
        int minPDist = int.MaxValue;
        for (int i = 0; i < places.Count; i++)
        {
            if (!places[i].Category.Equals("food", StringComparison.OrdinalIgnoreCase)) continue;
            int d = Math.Abs(i - targetPos);
            if (d < minPDist) { minPDist = d; foodIdx = i; }
        }
        if (foodIdx < 0) return;

        // Simple swap: food place ↔ target place
        (places[targetPos], places[foodIdx]) = (places[foodIdx], places[targetPos]);
    }

    // ── Clock walk ────────────────────────────────────────────────────────────

    private async Task WalkDayClockAsync(
        List<Place> places, ScheduleResult result, int day,
        Dictionary<(Guid, Guid), TravelInfoDto> prefetched,
        CancellationToken ct)
    {
        var clock      = DayStart;
        bool overpacked = false;
        int orderIndex  = 0;

        for (int i = 0; i < places.Count; i++)
        {
            var place    = places[i];
            int duration = VisitDurationFor(place);

            // Accumulate travel from previous emitted stop BEFORE checking opening hours
            TravelInfoDto? travelInfo = null;
            if (orderIndex > 0)
            {
                // Find the last emitted stop to compute travel from
                var prevStop = result.Stops.LastOrDefault(s => s.DayNumber == day);
                var prevPlace = prevStop is not null
                    ? places.FirstOrDefault(p => p.Id == prevStop.PlaceId)
                    : null;

                if (prevPlace?.Latitude.HasValue == true && prevPlace.Longitude.HasValue &&
                    place.Latitude.HasValue && place.Longitude.HasValue)
                {
                    double dist = Haversine(
                        (double)prevPlace.Latitude.Value, (double)prevPlace.Longitude.Value,
                        (double)place.Latitude.Value, (double)place.Longitude.Value);
                    string mode = dist < 2 ? "walk" : "drive";

                    // Use pre-fetched result for consecutive pairs (the common case).
                    // Falls back to a live resolver call only when a prior stop was skipped
                    // (opening-hours check), producing a non-consecutive pair not in the dict.
                    if (!prefetched.TryGetValue((prevPlace.Id, place.Id), out travelInfo))
                        travelInfo = await ResolveTravelAsync(prevPlace, place, dist, mode, ct);

                    clock += TimeSpan.FromMinutes(travelInfo.duration_min);
                }
            }

            // Opening hours check — shift clock forward if needed, skip if no window fits
            var openingHours = OpeningHoursData.FromJsonDocument(place.OpeningHours);
            if (openingHours is not null)
            {
                if (!openingHours.IsOpenAt(clock))
                {
                    var next = openingHours.NextOpenAt(clock);
                    if (next is null)
                    {
                        // No window left in the day — skip this place
                        AddWarningOnce(result.Warnings, "place_closed_skipped");
                        _logger.LogInformation(
                            "Builder: schedule day={Day} place={Place} skipped (closed, no later window)",
                            day, place.Name);
                        continue;
                    }
                    // Advance clock to next opening — travel info absorbed into the shift
                    clock = next.Value;
                    travelInfo = null; // gap absorbed by opening wait
                }
            }

            if (!overpacked && clock > DaySoftCap)
            {
                AddWarningOnce(result.Warnings, "day_overpacked");
                overpacked = true;
            }

            int h       = Math.Min((int)Math.Floor(clock.TotalHours), 23);
            int m       = h < 23 ? clock.Minutes : 59;
            string arrival = $"{h:D2}:{m:D2}";

            _logger.LogInformation(
                "Builder: schedule day={Day} stop={I} place={Place} arrival={Arrival} block={Block} dur={Dur}",
                day, orderIndex, place.Name, arrival, DeriveTimeBlock(clock), duration);

            result.Stops.Add(new ScheduledStopDto
            {
                PlaceId              = place.Id,
                DayNumber            = day,
                OrderIndex           = orderIndex,
                TimeBlock            = DeriveTimeBlock(clock),
                SuggestedArrival     = arrival,
                SuggestedDurationMin = duration,
                TravelFromPrevious   = travelInfo
            });

            clock += TimeSpan.FromMinutes(duration);
            orderIndex++;
        }
    }

    private async Task<TravelInfoDto> ResolveTravelAsync(Place from, Place to, double dist, string mode, CancellationToken ct)
    {
        if (_resolver != null)
        {
            try
            {
                var routeMode = mode == "walk" ? RoutingMode.Walking : RoutingMode.Driving;
                var segment = await _resolver.ResolveSegmentAsync(from, to, routeMode, ct);
                if (segment != null)
                    return new TravelInfoDto
                    {
                        distance_km  = Math.Round(segment.DistanceMeters / 1000.0, 1),
                        duration_min = Math.Max(1, (int)Math.Ceiling(segment.DurationSeconds / 60.0)),
                        mode         = mode
                    };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Catches non-OCE errors (HttpRequestException, DB failures…) and OCE from
                // internal timeouts. Does NOT catch OCE when ct is cancelled (user cancelled).
                _logger.LogWarning(ex, "Routing failed for segment {From}→{To} — using Haversine estimate",
                    from.Name, to.Name);
            }
        }

        return new TravelInfoDto
        {
            distance_km  = Math.Round(dist, 1),
            duration_min = EstimateTravelTime(dist, mode),
            mode         = mode
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string DeriveTimeBlock(TimeSpan clock)
    {
        double h = clock.TotalHours;
        if (h < 11)   return "morning";
        if (h < 14.5) return "lunch";
        if (h < 17.5) return "afternoon";
        if (h < 21)   return "dinner";
        return "evening";
    }

    private static int VisitDurationFor(Place p) =>
        p.VisitDurationMin ?? CategoryDurationMin.GetValueOrDefault(p.Category, DefaultDurationMin);

    private static TimeSpan[] SimulateArrivals(List<Place> places)
    {
        var arrivals = new TimeSpan[places.Count];
        var clock    = DayStart;
        for (int i = 0; i < places.Count; i++)
        {
            arrivals[i] = clock;
            int duration = VisitDurationFor(places[i]);
            if (i < places.Count - 1)
            {
                var cur  = places[i];
                var next = places[i + 1];
                if (cur.Latitude.HasValue && cur.Longitude.HasValue &&
                    next.Latitude.HasValue && next.Longitude.HasValue)
                {
                    double dist  = Haversine(
                        (double)cur.Latitude.Value, (double)cur.Longitude.Value,
                        (double)next.Latitude.Value, (double)next.Longitude.Value);
                    string mode  = dist < 2 ? "walk" : "drive";
                    duration    += EstimateTravelTime(dist, mode);
                }
            }
            clock += TimeSpan.FromMinutes(duration);
        }
        return arrivals;
    }

    private static void AddWarningOnce(List<string> warnings, string w)
    {
        if (!warnings.Contains(w)) warnings.Add(w);
    }

    // ── Legacy helpers (kept for existing tests and callers) ──────────────────

    internal List<Place> ApplyRefinements(List<Place> places, ExtractedPreferences prefs, ScheduleResult result)
    {
        var current = places;

        // Exclusions
        var exclusions = prefs.Exclusions;
        if (exclusions != null && exclusions.Count > 0)
        {
            var filtered = current
                .Where(p => !exclusions.Any(e =>
                    string.Equals(p.Category, e, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (filtered.Count == 0 && current.Count > 0)
            {
                _logger.LogWarning(
                    "Builder: exclusion_fallback exclusions=[{Ex}] totalPlaces={N} — using all",
                    string.Join(",", exclusions), current.Count);
                result.Warnings.Add("exclusion_fallback");
            }
            else
            {
                current = filtered;
                result.AppliedRefinements.Add($"excluded:{string.Join(",", exclusions)}");
            }
        }

        // Dietary — soft filter: places with SuitableFor data must match;
        // places without SuitableFor are always included (no info = safe to include).
        var dietary = prefs.Dietary;
        if (dietary != null && dietary.Count > 0 &&
            !dietary.Contains("none", StringComparer.OrdinalIgnoreCase))
        {
            var withDietary = current
                .Where(p =>
                    p.SuitableFor == null || p.SuitableFor.Count == 0 ||
                    dietary.Any(d => p.SuitableFor.Any(sf =>
                        sf.Contains(d, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            if (withDietary.Count == 0 && current.Count > 0)
            {
                _logger.LogWarning(
                    "Builder: dietary_no_matches dietary=[{D}] — using all",
                    string.Join(",", dietary));
                result.Warnings.Add("dietary_no_matches");
            }
            else if (withDietary.Count < current.Count)
            {
                current = withDietary;
                result.AppliedRefinements.Add($"dietary:{string.Join(",", dietary)}");
            }
        }

        return current;
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

        if (place.BestTimes is not { Count: > 0 }) return true;
        if (place.BestTimes.Any(bt => string.IsNullOrEmpty(bt) || bt.ToLower() == "any")) return true;
        if (BestTimeMatches.TryGetValue(timeBlock, out var matchingTimes))
            return place.BestTimes.Any(bt => matchingTimes.Any(t => bt.ToLower().Contains(t)));

        return true;
    }

    public IEnumerable<ScheduledStopResult> ResolveStopPlaces(List<ScheduledStopDto> stops, List<Place> allPlaces)
    {
        var placeMap = allPlaces.ToDictionary(p => p.Id);

        return stops.Select(stop =>
        {
            placeMap.TryGetValue(stop.PlaceId, out var place);
            return new ScheduledStopResult(
                Id: Guid.NewGuid(),
                PlaceId: stop.PlaceId,
                DayNumber: stop.DayNumber,
                OrderIndex: stop.OrderIndex,
                TimeBlock: stop.TimeBlock,
                SuggestedArrival: stop.SuggestedArrival,
                SuggestedDurationMin: stop.SuggestedDurationMin,
                TravelFromPrevious: stop.TravelFromPrevious,
                Place: place != null
                    ? new ResolvedPlaceDto(
                        Id: place.Id,
                        Name: place.Name,
                        Category: place.Category,
                        Neighborhood: place.Neighborhood,
                        WhyThisPlace: place.WhyThisPlace,
                        PriceRange: place.PriceRange,
                        Photos: place.Photos,
                        Latitude: place.Latitude,
                        Longitude: place.Longitude)
                    : null);
        });
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R  = 6371;
        var dLat        = ToRad(lat2 - lat1);
        var dLon        = ToRad(lon2 - lon1);
        var a           = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                          Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                          Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c           = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
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
