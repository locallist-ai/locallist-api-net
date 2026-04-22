using LocalList.API.NET.Features.Builder.Shared;
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
    // Pesos rebalanceados en Parte C del plan Builder quality:
    // - Cosine baja 0.50→0.40 (RAG ya no es la única señal, hay más soft signals).
    // - Category baja 0.20→0.15 (sobreindexaba cuando Gemini devolvía category errónea).
    // - BestFor se mantiene en 0.15.
    // - SuitableFor NUEVO en 0.15 (match con groupType: family/family-kids filtra adults-only).
    // - AiVibe se mantiene en 0.10.
    // - NeighborhoodPenalty se mantiene en 0.05.
    // Total contributivo = 0.40 + 0.15 + 0.15 + 0.15 + 0.10 = 0.95. Penalty resta hasta 0.05.
    private const float WeightCosine = 0.4f;
    private const float WeightCategory = 0.15f;
    private const float WeightBestFor = 0.15f;
    private const float WeightSuitableFor = 0.15f;
    private const float WeightAiVibe = 0.1f;
    private const float WeightNeighborhoodPenalty = 0.05f;

    public readonly record struct ScoredCandidate(Place Place, float Score, ScoreBreakdown Breakdown);

    public readonly record struct ScoreBreakdown(
        float Similarity,
        float CategoryMatch,
        float BestForMatch,
        float SuitableForMatch,
        float AiVibeNormalized,
        float NeighborhoodPenalty)
    {
        public float Total =>
            WeightCosine * Similarity
            + WeightCategory * CategoryMatch
            + WeightBestFor * BestForMatch
            + WeightSuitableFor * SuitableForMatch
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
                ScoreSuitableForMatch(c.place, prefs),
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
                bd.SuitableForMatch, bd.AiVibeNormalized, penalty);
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

    // SuitableFor valida si el place es apropiado para el groupType que eligió el usuario.
    // Reglas:
    //   - groupType vacío → 1.0 (neutral, no penalizamos cuando no hay señal de contexto).
    //   - place.SuitableFor null/empty → 0.5 (catalog sin etiquetar, no-info no castiga).
    //   - family/family-kids + place contiene family/kids/all-ages → 1.0.
    //   - family/family-kids + place contiene adults-only/21+ → 0.0 (hard exclusion).
    //   - otros cases → 0.7 (match parcial/ambiguo).
    // El peso (0.15) hace que un match vs un no-match genere ~+0.105 de diferencia, suficiente
    // para reordenar en top-5 sin dominar sobre cosine similarity.
    private static float ScoreSuitableForMatch(Place place, ExtractedPreferences prefs)
    {
        if (string.IsNullOrWhiteSpace(prefs.GroupType)) return 1f;

        var suitable = place.SuitableFor;
        if (suitable == null || suitable.Count == 0) return 0.5f;

        if (GroupTypePolicy.IsFamilyContext(prefs.GroupType))
        {
            var hasAdultsOnly = suitable.Any(s =>
                string.Equals(s, "adults-only", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "21+", StringComparison.OrdinalIgnoreCase));
            if (hasAdultsOnly) return 0f;

            var hasFamilyTag = suitable.Any(s =>
                string.Equals(s, "family", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "kids", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "all-ages", StringComparison.OrdinalIgnoreCase));
            if (hasFamilyTag) return 1f;

            return 0.5f; // no señal family ni adults-only, neutral-ish
        }

        return 0.7f;
    }

    private static float ScoreAiVibe(Place place)
    {
        // aiVibeScore es int? en 0..100 (de la auditoría). Normalizamos a [0..1].
        if (!place.AiVibeScore.HasValue) return 0f;
        var v = Math.Clamp(place.AiVibeScore.Value, 0, 100);
        return v / 100f;
    }
}
