using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Builder.Services;

/// <summary>
/// Rerank determinista en memoria sobre candidatos top-K devueltos por retrieval
/// semántico (cosine distance). Combina señal del LLM (embedding similarity)
/// con señales determinísticas del catálogo (categoría, bestFor, aiVibeScore,
/// diversidad de neighborhood) usando los pesos documentados en el plan RAG.
///
/// Puro (sin DI, sin DB). Los candidatos llegan con su `distance` (0..2) de
/// `CosineDistance`; internamente se convierte a similarity = 1 - distance.
/// </summary>
public class PlaceRankingService
{
    // Pesos tal como documentados en ~/.claude/plans/teniamos-un-plan-...
    private const float WeightCosine = 0.5f;
    private const float WeightCategory = 0.2f;
    private const float WeightBestFor = 0.15f;
    private const float WeightAiVibe = 0.1f;
    private const float WeightNeighborhoodPenalty = 0.05f;

    public readonly record struct ScoredCandidate(Place Place, float Score, ScoreBreakdown Breakdown);

    public readonly record struct ScoreBreakdown(
        float Similarity,
        float CategoryMatch,
        float BestForMatch,
        float AiVibeNormalized,
        float NeighborhoodPenalty)
    {
        public float Total =>
            WeightCosine * Similarity
            + WeightCategory * CategoryMatch
            + WeightBestFor * BestForMatch
            + WeightAiVibe * AiVibeNormalized
            - WeightNeighborhoodPenalty * NeighborhoodPenalty;
    }

    /// <summary>
    /// Rerank candidatos y devuelve la lista de <see cref="Place"/> ordenados desc por score.
    /// </summary>
    /// <param name="candidates">(place, cosine distance) — distance en [0..2] (0 = idéntico).</param>
    /// <param name="prefs">Preferences extraídas por Gemini del mensaje del usuario.</param>
    /// <returns>Lista ordenada por score desc. Misma longitud que input.</returns>
    public List<Place> Rank(
        IReadOnlyList<(Place place, float distance)> candidates,
        ExtractedPreferences prefs)
    {
        return RankInternal(candidates, prefs).Select(s => s.Place).ToList();
    }

    /// <summary>Exposes scoring for tests/debug — same order as <see cref="Rank"/>.</summary>
    public List<ScoredCandidate> RankWithScores(
        IReadOnlyList<(Place place, float distance)> candidates,
        ExtractedPreferences prefs)
    {
        return RankInternal(candidates, prefs);
    }

    /// <summary>
    /// Core del rerank. Dos pasadas:
    ///   1) Score preliminar sin penalty, ordenado desc.
    ///      Con tie-breaker determinista por `Place.Id` para evitar orden no-estable
    ///      cuando dos candidatos tienen mismo score.
    ///   2) Penalty por neighborhood: el primer candidato de cada neighborhood no
    ///      penaliza; los siguientes sí. Reordenamos por score final.
    /// </summary>
    private List<ScoredCandidate> RankInternal(
        IReadOnlyList<(Place place, float distance)> candidates,
        ExtractedPreferences prefs)
    {
        if (candidates.Count == 0) return new List<ScoredCandidate>();

        var preliminary = candidates.Select(c =>
        {
            var similarity = Math.Clamp(1f - c.distance, 0f, 1f);
            var bd = new ScoreBreakdown(
                similarity,
                ScoreCategoryMatch(c.place, prefs),
                ScoreBestForMatch(c.place, prefs),
                ScoreAiVibe(c.place),
                0f);
            return (c.place, bd);
        })
        .OrderByDescending(t => t.bd.Total)
        .ThenBy(t => t.place.Id) // deterministic tie-breaker
        .ToList();

        var seenNeighborhoods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scored = new List<ScoredCandidate>(candidates.Count);
        foreach (var (place, bd) in preliminary)
        {
            var penalty = 0f;
            if (!string.IsNullOrWhiteSpace(place.Neighborhood)
                && !seenNeighborhoods.Add(place.Neighborhood))
            {
                penalty = 1f; // flat penalty, multiplicado por WeightNeighborhoodPenalty = 0.05
            }
            var finalBd = new ScoreBreakdown(
                bd.Similarity, bd.CategoryMatch, bd.BestForMatch,
                bd.AiVibeNormalized, penalty);
            scored.Add(new ScoredCandidate(place, finalBd.Total, finalBd));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Place.Id) // deterministic tie-breaker
            .ToList();
    }

    private static float ScoreCategoryMatch(Place place, ExtractedPreferences prefs)
    {
        if (prefs.Categories == null || prefs.Categories.Count == 0) return 0f;
        return prefs.Categories.Any(c =>
            string.Equals(place.Category, c, StringComparison.OrdinalIgnoreCase))
            ? 1f
            : 0f;
    }

    private static float ScoreBestForMatch(Place place, ExtractedPreferences prefs)
    {
        if (place.BestFor == null || place.BestFor.Count == 0) return 0f;
        if (prefs.Vibes == null || prefs.Vibes.Count == 0) return 0f;

        var intersect = prefs.Vibes
            .Count(v => place.BestFor.Any(bf =>
                string.Equals(bf, v, StringComparison.OrdinalIgnoreCase)));
        return Math.Min(1f, intersect / (float)Math.Max(1, prefs.Vibes.Count));
    }

    private static float ScoreAiVibe(Place place)
    {
        // aiVibeScore es int? en 0..100 (de la auditoría). Normalizamos a [0..1].
        if (!place.AiVibeScore.HasValue) return 0f;
        var v = Math.Clamp(place.AiVibeScore.Value, 0, 100);
        return v / 100f;
    }
}
