using LocalList.API.NET.Features.Builder.Shared;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;

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
    // Pesos normalizados — suma de todos los pesos positivos = 1.0 exacto.
    // Orden de prioridad: cosine (señal semántica del LLM) > tier-2 (category/bestFor/
    // suitableFor/aiVibe) > soft signals (subcategory/company/style/budget).
    //
    // Historia de cambios:
    // - Parte B: Cosine 0.50→0.40, Category 0.20→0.15; SuitableFor NUEVO 0.15; AiVibe 0.10.
    // - Parte C: añadidos soft signals (Subcategory 0.10, CompanyTags/StyleTags/Budget 0.05)
    //   → suma total subió a 1.20 (deuda técnica DT-5).
    // - 2026-06-09: renormalizar todo a 1.0; WeightNeighborhoodPenalty (subtractivo) = 0.04
    //   → rango efectivo del score final: [-0.04, 1.0].
    // - 2026-07-13 (fix plan-quality): Budget 0.04→0.10 (el tier elegido por el usuario
    //   apenas movía el ranking); compensado con Cosine 0.34→0.30 y StyleTags 0.05→0.03
    //   (StyleTags nunca se puebla desde TripContextDto — peso casi muerto).
    private const float WeightCosine = 0.30f;
    private const float WeightCategory = 0.12f;
    private const float WeightBestFor = 0.12f;
    private const float WeightSuitableFor = 0.12f;
    private const float WeightAiVibe = 0.08f;
    private const float WeightNeighborhoodPenalty = 0.04f;
    private const float WeightSubcategory = 0.08f;
    private const float WeightCompanyTags = 0.05f;
    private const float WeightStyleTags = 0.03f;
    private const float WeightBudget = 0.10f;

    public readonly record struct ScoredCandidate(Place Place, float Score, ScoreBreakdown Breakdown);

    public readonly record struct ScoreBreakdown(
        float Similarity,
        float CategoryMatch,
        float BestForMatch,
        float SuitableForMatch,
        float AiVibeNormalized,
        float NeighborhoodPenalty,
        float SubcategoryMatch,
        float CompanyTagsMatch,
        float StyleTagsMatch,
        float BudgetMatch)
    {
        public float Total =>
            WeightCosine * Similarity
            + WeightCategory * CategoryMatch
            + WeightBestFor * BestForMatch
            + WeightSuitableFor * SuitableForMatch
            + WeightAiVibe * AiVibeNormalized
            + WeightSubcategory * SubcategoryMatch
            + WeightCompanyTags * CompanyTagsMatch
            + WeightStyleTags * StyleTagsMatch
            + WeightBudget * BudgetMatch
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
                0f,
                ScoreSubcategoryMatch(c.place, prefs),
                ScoreCompanyTagsMatch(c.place, prefs),
                ScoreStyleTagsMatch(c.place, prefs),
                ScoreBudgetMatch(c.place, prefs));
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
                penalty = 1f; // flat penalty, multiplicado por WeightNeighborhoodPenalty = 0.04
            }
            var finalBd = new ScoreBreakdown(
                bd.Similarity, bd.CategoryMatch, bd.BestForMatch,
                bd.SuitableForMatch, bd.AiVibeNormalized, penalty,
                bd.SubcategoryMatch, bd.CompanyTagsMatch,
                bd.StyleTagsMatch, bd.BudgetMatch);
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
    // El peso (0.12) hace que un match vs un no-match genere ~+0.084 de diferencia, suficiente
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

    // Subcategory drill-down match (Pablo 2026-04-25/27).
    // prefs.Subcategories es { "food": ["sushi","italian"], ... }. Para cada
    // place, buscamos el bucket que coincide con su Category y validamos
    // que algún tag del bucket aparezca como substring en place.Subcategory.
    // Returns:
    //   - 0.0 si no hay subcategorías para esta Category (no info, neutral).
    //   - 1.0 si algún tag matchea por substring (case-insensitive).
    //   - 0.0 si hay tags pero ninguno matchea (place no encaja con drill-down).
    // El catálogo Miami tiene Subcategory descriptiva como "Coastal Italian /
    // seafood" — substring match captura "italian" + "seafood" sin perder.
    private static float ScoreSubcategoryMatch(Place place, ExtractedPreferences prefs)
    {
        if (prefs.Subcategories == null || prefs.Subcategories.Count == 0) return 0f;
        if (string.IsNullOrWhiteSpace(place.Category)) return 0f;

        var placeSubs = place.Subcategories is { Count: > 0 } ? place.Subcategories : null;
        if (placeSubs == null || placeSubs.Count == 0) return 0f;

        List<string>? tags = null;
        foreach (var (key, value) in prefs.Subcategories)
        {
            if (string.Equals(key, place.Category, StringComparison.OrdinalIgnoreCase))
            {
                tags = value;
                break;
            }
        }
        if (tags == null || tags.Count == 0) return 0f;

        return placeSubs.Any(sub =>
            tags.Any(t => !string.IsNullOrWhiteSpace(t) && sub.ToLowerInvariant().Contains(t.ToLowerInvariant())))
            ? 1f
            : 0f;
    }

    // CompanyTags refinement match — ej. usuario eligió couple → honeymoon.
    // Se cruza con place.SuitableFor + place.BestFor (algunos catálogos taggean
    // "honeymoon", "kids", "anniversary" en bestFor; otros en suitableFor).
    // Returns 1.0 si algún tag matchea exact (case-insensitive), else 0.
    private static float ScoreCompanyTagsMatch(Place place, ExtractedPreferences prefs)
    {
        if (prefs.CompanyTags == null || prefs.CompanyTags.Count == 0) return 0f;

        bool MatchesAny(IEnumerable<string>? haystack)
        {
            if (haystack == null) return false;
            return haystack.Any(h => prefs.CompanyTags!.Any(t =>
                string.Equals(h, t, StringComparison.OrdinalIgnoreCase)));
        }

        return MatchesAny(place.SuitableFor) || MatchesAny(place.BestFor) ? 1f : 0f;
    }

    // StyleTags refinement match — ej. usuario eligió adventure → ["urban","foodie"].
    // Cruza con place.BestFor (urban-explorer, foodie, outdoor, etc).
    private static float ScoreStyleTagsMatch(Place place, ExtractedPreferences prefs)
    {
        if (prefs.StyleTags == null || prefs.StyleTags.Count == 0) return 0f;
        if (place.BestFor == null || place.BestFor.Count == 0) return 0f;

        var matches = prefs.StyleTags.Count(t => place.BestFor.Any(b =>
            !string.IsNullOrWhiteSpace(b)
            && (string.Equals(b, t, StringComparison.OrdinalIgnoreCase)
                || b.ToLowerInvariant().Contains(t.ToLowerInvariant()))));
        return matches > 0 ? Math.Min(1f, matches / (float)prefs.StyleTags.Count) : 0f;
    }

    // Budget match — compara la banda de tiers deseada con place.PriceRange
    // ("$"/"$$"/"$$$"/"$$$$"). Dos fuentes, por orden de precisión:
    //   1. BudgetAmount (USD/día/persona, custom input): <80 → $, 80-199 → $$,
    //      200-399 → $$$, >=400 → $$$$ (banda de un solo tier).
    //   2. BudgetTier del wizard: budget → $/$$, moderate → $$/$$$, premium → $$$/$$$$.
    // Returns 1.0 si el place cae en la banda, 0.6 si está a 1 tier, 0 si discrepa más.
    private static float ScoreBudgetMatch(Place place, ExtractedPreferences prefs)
    {
        (int Min, int Max)? band = null;
        if (prefs.BudgetAmount.HasValue)
        {
            var amount = prefs.BudgetAmount.Value;
            int desiredTier = amount < 80 ? 1 : amount < 200 ? 2 : amount < 400 ? 3 : 4;
            band = (desiredTier, desiredTier);
        }
        else if (!string.IsNullOrWhiteSpace(prefs.BudgetTier))
        {
            band = prefs.BudgetTier.ToLowerInvariant() switch
            {
                "budget"   => (1, 2),
                "moderate" => (2, 3),
                "premium"  => (3, 4),
                _          => ((int, int)?)null,
            };
        }
        if (band is null) return 0f;
        if (string.IsNullOrWhiteSpace(place.PriceRange)) return 0.5f; // sin info, neutral

        // tier del place por count de '$'
        int placeTier = place.PriceRange.Count(c => c == '$');
        if (placeTier == 0) return 0.5f;

        int diff = placeTier < band.Value.Min ? band.Value.Min - placeTier
                 : placeTier > band.Value.Max ? placeTier - band.Value.Max
                 : 0;
        return diff switch
        {
            0 => 1f,
            1 => 0.6f,
            _ => 0f,
        };
    }
}
