using LocalList.API.NET.Features.Builder.Shared;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Builder.Services;

// Candidate selection: pace clamp (Step 1) and weighted sampling across days (Step 2).
// Logic is identical to the original single-file version.
public partial class SchedulingService
{
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
}
