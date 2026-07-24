using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Builder.Services;

// Day shaping performed BEFORE the clock walk: geographic ordering (nearest-neighbor),
// nightlife pushed to the end, and meal anchoring against simulated arrivals. Logic is
// identical to the original single-file version; only its location changed.
public partial class SchedulingService
{
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

    // NOTE (API-2 scope): meal anchoring simulates arrivals with Haversine estimates and does
    // NOT consult opening hours — m5 (nightlife may land mid-afternoon on short days) and m7
    // (Haversine vs the Mapbox clock used by the real walk) are deliberately out of scope here.
    // The real viability gate runs later in WalkDayClockAsync against the actual clock.
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
}
