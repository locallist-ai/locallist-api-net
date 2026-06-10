using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
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
public class PlanGenerationService
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
        CancellationToken ct)
    {
        tripContext ??= new TripContextDto();
        var msg = message ?? string.Empty;
        var city = tripContext.City ?? "Miami";

        _logger.LogInformation(
            "PlanGen: city={City} days={Days} groupType={GT} categories={Cats} budget={Budget} msgLen={Len}",
            city, tripContext.Days, tripContext.GroupType,
            tripContext.Categories == null ? "(null)" : string.Join(",", tripContext.Categories),
            tripContext.Budget, msg.Length);

        var (prefs, geminiDiag) = await _aiProvider.ExtractPreferencesAsync(msg, tripContext, lang, ct);

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

        var seed = Random.Shared.Next();
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
            GeminiDiagnostics = geminiDiag,
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

        var ranked = _ranker.RankWithScores(
            candidates.Select(c => (c.Place, c.Distance)).ToList(),
            prefs);

        _logger.LogInformation(
            "RAG: city={City} query='{Q}' topK={K} top3=[{Top}]",
            city,
            queryText.Length > 60 ? queryText[..60] + "…" : queryText,
            ranked.Count,
            string.Join("|", ranked.Take(3).Select(s => $"{s.Place.Name}:{s.Score:F2}")));

        return ranked.Select(s => s.Place).ToList();
    }

    private async Task<List<Place>> FallbackKeywordFilterAsync(
        string city, ExtractedPreferences prefs, CancellationToken ct)
    {
        // OrderBy(p => p.Id) is load-bearing for determinism: same seed + same city always
        // produces the same candidate pool because FilterByCategory preserves input order.
        var matching = await _db.Places.AsNoTracking()
            .Where(p => p.Status == "published" && p.City == city)
            .OrderBy(p => p.Id)
            .Take(FallbackKeywordHardCap)
            .ToListAsync(ct);
        return FilterByCategory(matching, prefs);
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

    private static string Sanitize(string value, int maxLen)
    {
        if (string.IsNullOrEmpty(value)) return value;
        // Strip control chars (including null bytes), cap length
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > maxLen ? clean[..maxLen] : clean;
    }
}

public class PlanGenerationResult
{
    public required ExtractedPreferences Prefs { get; init; }
    public required ScheduleResult Schedule { get; init; }
    public required List<Place> FilteredPlaces { get; init; }
    public required string PlanName { get; init; }
    public required string PlanDescription { get; init; }
    public required string City { get; init; }
    public required string Lang { get; init; }
    public AiCallDiagnostics? GeminiDiagnostics { get; init; }
}
