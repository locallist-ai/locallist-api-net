using LocalList.API.NET.Shared.AI.Services;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.Observability;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace LocalList.API.NET.Features.Builder.Services;

/// <summary>
/// Core plan generation pipeline shared by /builder/chat and /chat/generate.
/// Handles: AI preference extraction → RAG retrieval → ranking → scheduling → naming.
/// Does NOT persist to DB — the caller decides ownership and persistence.
/// </summary>
public class PlanGenerationService : IPlanGenerationService
{
    private readonly LocalListDbContext _db;
    private readonly PreferenceExtractorService _aiProvider;
    private readonly EmbeddingService _embeddings;
    private readonly PlaceRankingService _ranker;
    private readonly SchedulingService _scheduler;
    private readonly ILogger<PlanGenerationService> _logger;

    private const int MinEmbeddedPlacesForRag = 3;
    private const int RetrievalTopK = 50;
    private const int FallbackKeywordHardCap = 500;

    // Plan content length limits — sanitize Gemini output before persisting
    public const int MaxPlanNameLength = 200;
    public const int MaxPlanDescriptionLength = 1000;

    public PlanGenerationService(
        LocalListDbContext db,
        PreferenceExtractorService aiProvider,
        EmbeddingService embeddings,
        PlaceRankingService ranker,
        SchedulingService scheduler,
        ILogger<PlanGenerationService> logger)
    {
        _db = db;
        _aiProvider = aiProvider;
        _embeddings = embeddings;
        _ranker = ranker;
        _scheduler = scheduler;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full plan generation pipeline. Returns null if the city has
    /// no published places (caller should surface a graceful error).
    /// </summary>
    public async Task<PlanGenerationResult?> GenerateAsync(
        string? message,
        TripContextDto? tripContext,
        string lang,
        int maxDays,
        CancellationToken ct)
    {
        tripContext ??= new TripContextDto();
        var msg = message ?? string.Empty;
        var city = tripContext.City ?? "Miami";

        _logger.LogInformation(
            "PlanGen: city={City} days={Days} maxDays={MaxDays} groupType={GT} categories={Cats} budget={Budget} msgLen={Len}",
            city, tripContext.Days, maxDays, tripContext.GroupType,
            tripContext.Categories == null ? "(null)" : string.Join(",", tripContext.Categories),
            tripContext.Budget, msg.Length);

        var (prefs, llmDiag) = await _aiProvider.ExtractPreferencesAsync(msg, tripContext, lang, ct);

        // Clamp de tier (F4): los días pedidos explícitamente ya pasaron el gate del
        // controller, pero el LLM puede derivar Days del texto libre ("10 días en Miami").
        // Acotar aquí — después de la extracción y antes de scheduling/naming — garantiza
        // que NINGÚN camino produce un plan más largo que el techo del tier.
        if (prefs.Days > maxDays)
        {
            _logger.LogInformation(
                "PlanGen: clamping extracted days {Days} -> {MaxDays} (tier cap)", prefs.Days, maxDays);
            prefs.Days = maxDays;
        }

        _logger.LogInformation(
            "PlanGen: prefs days={Days} cats={Cats} vibes={Vibes} maxStops={Max} name='{Name}'",
            prefs.Days,
            string.Join(",", prefs.Categories),
            string.Join(",", prefs.Vibes),
            prefs.MaxStopsPerDay,
            prefs.PlanName);

        var places = await RetrieveCandidatesAsync(msg, city, prefs, ct);

        if (places.Count == 0)
        {
            _logger.LogWarning("PlanGen: zero candidates for city={City}", city);
            return null;
        }

        // Semilla determinista derivada del request: fija la selección y el orden
        // de candidatos del scheduler (contrato "determinista por semilla" del
        // scheduler, dado un conjunto de candidatos). NO garantiza plan idéntico
        // end-to-end en la ruta RAG por dos motivos: (1) la query de embedding
        // incorpora prefs.Vibes, extraídas por el LLM con temperatura > 0; (2) el
        // texto de esa query es sensible al ORDEN de serialización de categorías/
        // vibes (ver RetrieveCandidatesAsync ~L141-142) — no se canonicaliza aquí
        // porque cambiar el orden alteraría el embedding y no se ha evaluado su
        // efecto en la calidad del plan. La semilla SÍ canonicaliza el orden para
        // la selección del scheduler, y la ruta keyword-fallback es determinista
        // end-to-end. Antes la semilla era Random.Shared.Next(), que hacía cada
        // regeneración una lotería aun con el mismo pool y ocultaba los parámetros
        // del usuario tras el muestreo aleatorio.
        var seed = ComputeRequestSeed(msg, city, lang, tripContext);
        _logger.LogInformation("PlanGen: schedule seed={Seed}", seed);
        var schedule = await _scheduler.BuildPlanScheduleAsync(places, prefs, seed, ct);

        // Sanitize Gemini-generated text before returning (L6 defense)
        var planName = Sanitize(PlanNamingService.BuildPlanName(prefs, city, msg), MaxPlanNameLength);
        var planDescription = Sanitize(
            !string.IsNullOrEmpty(prefs.Description) ? prefs.Description : PlanNamingService.BuildPlanDescription(prefs),
            MaxPlanDescriptionLength);

        _logger.LogInformation(
            "PlanGen: schedule stops={N} warnings=[{W}]",
            schedule.Stops.Count,
            string.Join(",", schedule.Warnings));

        return new PlanGenerationResult
        {
            Prefs = prefs,
            Schedule = schedule,
            FilteredPlaces = places,
            PlanName = planName,
            PlanDescription = planDescription,
            City = city,
            Lang = lang,
            LlmDiagnostics = llmDiag,
        };
    }

    // ── RAG pipeline ─────────────────────────────────────────────────────────

    private async Task<List<Place>> RetrieveCandidatesAsync(
        string rawMessage, string city, ExtractedPreferences prefs, CancellationToken ct)
    {
        var embeddedCount = await _db.Places.AsNoTracking()
            .CountAsync(p => p.Status == "published" && p.City == city && p.Embedding != null, ct);

        if (embeddedCount < MinEmbeddedPlacesForRag)
        {
            _logger.LogInformation(
                "RAG fallback (keyword): city={City} embeddedCount={Count} < {Min}",
                city, embeddedCount, MinEmbeddedPlacesForRag);
            return await FallbackKeywordFilterAsync(city, prefs, ct);
        }

        var queryParts = new List<string> { rawMessage };
        if (prefs.Categories.Count > 0) queryParts.Add(string.Join(" ", prefs.Categories));
        if (prefs.Vibes.Count > 0) queryParts.Add(string.Join(" ", prefs.Vibes));
        if (!string.IsNullOrWhiteSpace(prefs.GroupType)) queryParts.Add(prefs.GroupType);
        var queryText = string.Join(". ", queryParts.Where(p => !string.IsNullOrWhiteSpace(p)));

        Vector? qvec;
        try
        {
            qvec = await _embeddings.EmbedAsync(queryText, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Embedding failed — RAG fallback. city={City}", city);
            return await FallbackKeywordFilterAsync(city, prefs, ct);
        }

        if (qvec == null)
        {
            _logger.LogWarning("EmbedAsync returned null — RAG fallback. city={City}", city);
            return await FallbackKeywordFilterAsync(city, prefs, ct);
        }

        var candidates = await _db.Places.AsNoTracking()
            .Where(p => p.Status == "published" && p.City == city && p.Embedding != null)
            .OrderBy(p => p.Embedding!.CosineDistance(qvec))
            .Take(RetrievalTopK)
            .Select(p => new { Place = p, Distance = (float)p.Embedding!.CosineDistance(qvec) })
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            _logger.LogInformation("RAG: 0 candidates for city={City} — keyword fallback", city);
            return await FallbackKeywordFilterAsync(city, prefs, ct);
        }

        // Top-up por categoría explícita: el top-K por cosine se calcula sobre TODO
        // el catálogo de la ciudad, así que en ciudades grandes los places de la
        // categoría pedida pueden quedar fuera del top-K y disparar el fallback
        // mixto del gate aunque el catálogo sí tenga suficientes. Segunda query
        // restringida a las categorías pedidas (+ food, exenta del gate para los
        // meal slots) para que el gate vea el catálogo real de la categoría.
        // Mismas semánticas de match que FilterByCategory (substring, case-insensitive).
        if (prefs.CategoriesExplicit && prefs.Categories.Count > 0)
        {
            var cats = prefs.Categories
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.ToLowerInvariant())
                .ToList();
            if (!cats.Contains("food")) cats.Add("food");

            var seenIds = candidates.Select(c => c.Place.Id).ToHashSet();
            var topUp = await _db.Places.AsNoTracking()
                .Where(p => p.Status == "published" && p.City == city && p.Embedding != null)
                .Where(p => cats.Any(c => p.Category.ToLower().Contains(c)))
                .OrderBy(p => p.Embedding!.CosineDistance(qvec))
                .Take(RetrievalTopK)
                .Select(p => new { Place = p, Distance = (float)p.Embedding!.CosineDistance(qvec) })
                .ToListAsync(ct);

            var added = topUp.Where(t => !seenIds.Contains(t.Place.Id)).ToList();
            if (added.Count > 0)
            {
                candidates.AddRange(added);
                _logger.LogInformation(
                    "RAG: category top-up [{Cats}] +{Added} candidates ({Total} total)",
                    string.Join(",", cats), added.Count, candidates.Count);
            }
        }

        var ranked = _ranker.RankWithScores(
            candidates.Select(c => (c.Place, c.Distance)).ToList(),
            prefs);

        _logger.LogInformation(
            "RAG: city={City} query='{Q}' topK={K} top3=[{Top}]",
            city,
            queryText.Length > 60 ? queryText[..60] + "…" : queryText,
            ranked.Count,
            string.Join("|", ranked.Take(3).Select(s => $"{s.Place.Name}:{s.Score:F2}")));

        var rankedPlaces = ranked.Select(s => s.Place).ToList();

        // Gate duro cuando las categorías son elección explícita del usuario (wizard/
        // chat slots) — alinea la ruta RAG con la keyword, donde categoría ya filtra.
        // Con categorías solo-LLM (inferidas del mensaje) se mantiene el boost blando
        // del ranking, sin recortar el recall semántico.
        if (prefs.CategoriesExplicit && prefs.Categories.Count > 0)
        {
            var gated = ApplyCategoryGate(rankedPlaces, prefs);
            _logger.LogInformation(
                "RAG: category gate [{Cats}] {Before}→{After} candidates",
                string.Join(",", prefs.Categories), rankedPlaces.Count, gated.Count);
            return gated;
        }

        return rankedPlaces;
    }

    internal async Task<List<Place>> FallbackKeywordFilterAsync(
        string city, ExtractedPreferences prefs, CancellationToken ct)
    {
        // OrderBy(p => p.Id) is load-bearing for determinism: same seed + same city always
        // produces the same candidate pool because FilterByCategory preserves input order.
        var pool = await _db.Places.AsNoTracking()
            .Where(p => p.Status == "published" && p.City == city)
            .OrderBy(p => p.Id)
            .Take(FallbackKeywordHardCap)
            .ToListAsync(ct);
        return ApplyCategoryGate(pool, prefs);
    }

    internal static List<Place> FilterByCategory(List<Place> allPlaces, ExtractedPreferences prefs)
    {
        if (prefs.Categories == null || !prefs.Categories.Any()) return allPlaces;

        return allPlaces.Where(p =>
            prefs.Categories.Any(c =>
                string.Equals(p.Category, c, StringComparison.OrdinalIgnoreCase)
                || p.Category.Contains(c, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// Filtro duro por categoría con fallback graceful: si el catálogo no tiene
    /// suficientes places de las categorías pedidas para llenar el plan
    /// (days × maxStops efectivos, con el clamp de pace del scheduler), completa
    /// con el resto de candidatos manteniendo a los de la categoría pedida
    /// primero. Mejor un plan mixto que uno vacío.
    /// Food está exento del gate: EnsureFoodPerDay (scheduler) solo puede
    /// garantizar ≥1 parada de comida por día si el pool conserva candidatos
    /// food — un wizard con categories=["culture"] no debe producir días sin
    /// comer. Los food no pedidos van al final del pool: no ocupan los slots
    /// rank-first de la categoría elegida y quedan disponibles para los meal
    /// slots del scheduler.
    /// Preserva el orden de entrada (ranking o Id), que es load-bearing para el
    /// determinismo y para la selección rank-first del scheduler.
    ///
    /// LIMITACIÓN CONOCIDA (gap de escala, no aborda este PR): la exención de food
    /// solo protege a los food que YA están en <paramref name="orderedPlaces"/>.
    /// En la ruta RAG ese pool es el top-K por cosine (RetrievalTopK=50) más el
    /// top-up por categoría — que también está capado a RetrievalTopK. En una
    /// ciudad con catálogo grande dominado por la categoría pedida, los food
    /// pueden quedar fuera de ambos cortes y EnsureFoodPerDay no tendría nada que
    /// colocar → días sin comida. No dispara a escala Miami; es la misma clase de
    /// problema que "gate sobre top-50 vs catálogo" y se abordará cuando el
    /// catálogo crezca (p. ej. query dedicada de food garantizada, sin cap por
    /// cosine). La ruta keyword (FallbackKeywordHardCap=500 sin ranking) no sufre
    /// este corte.
    /// </summary>
    internal static List<Place> ApplyCategoryGate(List<Place> orderedPlaces, ExtractedPreferences prefs)
    {
        if (prefs.Categories == null || prefs.Categories.Count == 0) return orderedPlaces;

        var matching = FilterByCategory(orderedPlaces, prefs);
        var matchingIds = matching.Select(p => p.Id).ToHashSet();

        // Mismo cálculo de slots que el scheduler (pace clamp incluido) — si
        // divergen, el gate cree que faltan candidatos y mete el fallback mixto
        // en planes que el scheduler llenaría solo con la categoría pedida.
        int needed = Math.Max(1, prefs.Days * SchedulingService.ResolveEffectiveMaxStops(prefs));
        if (matching.Count >= needed)
        {
            var unmatchedFood = orderedPlaces
                .Where(p => !matchingIds.Contains(p.Id)
                            && p.Category.Equals("food", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return matching.Concat(unmatchedFood).ToList();
        }

        // Fallback mixto: el resto de candidatos ya incluye los food no pedidos.
        return matching
            .Concat(orderedPlaces.Where(p => !matchingIds.Contains(p.Id)))
            .ToList();
    }

    /// <summary>
    /// FNV-1a 32-bit sobre los campos del request. Estable entre procesos
    /// (string.GetHashCode está aleatorizado por proceso) — la misma petición
    /// siempre produce la misma semilla, por lo que la selección de candidatos
    /// del scheduler es reproducible (determinismo a nivel de scheduler dado un
    /// conjunto de candidatos fijo, no de la petición end-to-end: la query de
    /// embedding incorpora prefs.Vibes, que salen de la extracción LLM con
    /// temperatura > 0 y no son deterministas).
    /// Todas las listas se canonicalizan ordenadas (Ordinal), tanto las
    /// top-level como las CLAVES y los VALORES de cada bucket de Subcategories:
    /// el mismo set de selecciones del wizard produce la misma semilla aunque
    /// el cliente lo serialice en otro orden.
    /// </summary>
    internal static int ComputeRequestSeed(string message, string city, string lang, TripContextDto context)
    {
        var canonical = string.Join("|",
            message,
            city,
            lang,
            context.Days?.ToString() ?? "",
            context.GroupType ?? "",
            context.Budget ?? "",
            context.BudgetAmount?.ToString() ?? "",
            CanonicalList(context.Categories),
            context.Subcategories is null
                ? ""
                : string.Join(";", context.Subcategories
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{kv.Key}:{CanonicalList(kv.Value)}")),
            CanonicalList(context.CompanyTags),
            context.Pace ?? "",
            CanonicalList(context.Dietary),
            CanonicalList(context.Exclusions),
            context.VibesPrimary ?? "");

        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in canonical)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)(hash & int.MaxValue);
        }
    }

    private static string CanonicalList(List<string>? values) =>
        values is null ? "" : string.Join(",", values.OrderBy(v => v, StringComparer.Ordinal));

    private static string Sanitize(string value, int maxLen)
    {
        if (string.IsNullOrEmpty(value)) return value;
        // Strip control chars (including null bytes), cap length
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > maxLen ? clean[..maxLen] : clean;
    }

    public IEnumerable<ScheduledStopResult> ResolveStopPlaces(List<ScheduledStopDto> stops, List<Place> allPlaces) =>
        _scheduler.ResolveStopPlaces(stops, allPlaces);
}
