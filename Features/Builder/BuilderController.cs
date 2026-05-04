using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.I18n;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace LocalList.API.NET.Features.Builder;

[ApiController]
[Route("builder")]
public class BuilderController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly AiProviderService _aiProvider;
    private readonly EmbeddingService _embeddings;
    private readonly PlaceRankingService _ranker;
    private readonly SchedulingService _scheduler;
    private readonly ILogger<BuilderController> _logger;

    private const int MinEmbeddedPlacesForRag = 3;
    private const int RetrievalTopK = 50;

    public BuilderController(
        LocalListDbContext db,
        AiProviderService aiProvider,
        EmbeddingService embeddings,
        PlaceRankingService ranker,
        SchedulingService scheduler,
        ILogger<BuilderController> logger)
    {
        _db = db;
        _aiProvider = aiProvider;
        _embeddings = embeddings;
        _ranker = ranker;
        _scheduler = scheduler;
        _logger = logger;
    }

    [HttpPost("chat")]
    [AllowAnonymous]
    [EnableRateLimiting("BuilderLimit")]
    public async Task<IActionResult> GeneratePlan([FromBody] BuilderChatRequest request, CancellationToken ct)
    {
        var isAnonymous = !User.Identity?.IsAuthenticated ?? true;
        Guid? userId = isAnonymous ? null : await User.GetUserIdAsync(_db, ct);

        _logger.LogInformation(
            "Builder: request anonymous={IsAnon} city={City} days={Days} groupType={GroupType} categories={Categories} budget={Budget} msgLen={MsgLen}",
            isAnonymous,
            request.TripContext?.City ?? "(unset)",
            request.TripContext?.Days?.ToString() ?? "(unset)",
            request.TripContext?.GroupType ?? "(unset)",
            request.TripContext?.Categories == null ? "(null)" : string.Join(",", request.TripContext.Categories),
            request.TripContext?.Budget ?? "(unset)",
            request.Message?.Length ?? 0);

        var inputCheck = ValidateMinimumInput(request);
        if (!inputCheck.accepted)
        {
            _logger.LogInformation(
                "Builder: rejected insufficient_input — hasMsg={M} wizardCity={C} wizardDays={D} wizardGroup={G} wizardInterests={I} wizardBudget={B}",
                inputCheck.signals["chat_message"],
                inputCheck.signals["wizard_city"],
                inputCheck.signals["wizard_days"],
                inputCheck.signals["wizard_groupType"],
                inputCheck.signals["wizard_interests"],
                inputCheck.signals["wizard_budget"]);
            return BadRequest(new
            {
                error = "insufficient_input",
                message = "Please complete at least 3 wizard steps (city, duration, group, interests, budget).",
                signals = inputCheck.signals
            });
        }

        try
        {
            var message = request.Message ?? string.Empty;

            var lang = LanguageAccessor.ResolveRequestLanguage(Request);
            var prefs = await _aiProvider.ExtractPreferencesAsync(message, request.TripContext, lang, ct);

            _logger.LogInformation(
                "Builder: prefs days={Days} categories={Cats} vibes={Vibes} groupType={GT} planName='{Name}' maxStopsPerDay={Max}",
                prefs.Days,
                prefs.Categories == null ? "(null)" : string.Join(",", prefs.Categories),
                prefs.Vibes == null ? "(null)" : string.Join(",", prefs.Vibes),
                prefs.GroupType,
                prefs.PlanName,
                prefs.MaxStopsPerDay);

            var city = request.TripContext?.City ?? "Miami";
            var filteredPlaces = await RetrieveCandidatesAsync(message, city, prefs, ct);

            _logger.LogInformation("Builder: candidates count={Count}", filteredPlaces.Count);

            var schedule = _scheduler.BuildPlanSchedule(filteredPlaces, prefs);
            var planStopsData = schedule.Stops;

            var pickedPlacesById = filteredPlaces.ToDictionary(p => p.Id);
            var scheduleSummary = string.Join(" | ", planStopsData.Select(sd =>
            {
                pickedPlacesById.TryGetValue(sd.PlaceId, out var pl);
                return $"d{sd.DayNumber}.{sd.TimeBlock}→{pl?.Name ?? "?"}({pl?.Category ?? "?"})";
            }));
            var categoryMix = string.Join(",", planStopsData
                .Select(sd => pickedPlacesById.TryGetValue(sd.PlaceId, out var pl) ? pl.Category : "?")
                .GroupBy(c => c)
                .Select(g => $"{g.Key}:{g.Count()}"));
            _logger.LogInformation(
                "Builder: schedule days={Days} stops={N} categoryMix=[{Mix}] plan=[{Schedule}] warnings=[{Warnings}]",
                prefs.Days, planStopsData.Count, categoryMix, scheduleSummary,
                string.Join(",", schedule.Warnings));

            var planName = BuildPlanName(prefs, city, message);
            var planDescription = !string.IsNullOrEmpty(prefs.Description)
                ? prefs.Description
                : BuildPlanDescription(prefs);

            if (isAnonymous)
            {
                var ephemeralPlan = new
                {
                    Id = Guid.NewGuid(),
                    Name = planName,
                    City = city,
                    Type = "ai",
                    Description = planDescription,
                    DurationDays = prefs.Days,
                    TripContext = request.TripContext,
                    IsPublic = false,
                    IsEphemeral = true
                };

                return Ok(new
                {
                    plan = ephemeralPlan,
                    stops = _scheduler.ResolveStopPlaces(planStopsData, filteredPlaces),
                    message = $"Created a {prefs.Days}-day plan with {planStopsData.Count} stops!",
                    warnings = schedule.Warnings
                });
            }

            var plan = new Plan
            {
                Name = planName,
                City = city,
                Type = "ai",
                Description = planDescription,
                DurationDays = prefs.Days,
                TripContext = request.TripContext != null
                    ? JsonSerializer.SerializeToDocument(request.TripContext)
                    : JsonSerializer.SerializeToDocument(new {}),
                IsPublic = false,
                CreatedById = userId,
                NameI18n = LanguageAccessor.SetI18nString(null, lang, planName),
                DescriptionI18n = LanguageAccessor.SetI18nString(null, lang, planDescription),
            };

            _db.Plans.Add(plan);

            var stopsToInsert = planStopsData.Select(sd => new PlanStop
            {
                PlanId = plan.Id,
                PlaceId = sd.PlaceId,
                DayNumber = sd.DayNumber,
                OrderIndex = sd.OrderIndex,
                TimeBlock = sd.TimeBlock,
                SuggestedArrival = string.IsNullOrEmpty(sd.SuggestedArrival) ? null : TimeSpan.Parse(sd.SuggestedArrival),
                SuggestedDurationMin = sd.SuggestedDurationMin,
                TravelFromPrevious = sd.TravelFromPrevious != null ? JsonSerializer.SerializeToDocument(sd.TravelFromPrevious) : null
            }).ToList();

            if (stopsToInsert.Any())
                _db.PlanStops.AddRange(stopsToInsert);

            await _db.SaveChangesAsync(ct);

            return Ok(new
            {
                plan,
                stops = _scheduler.ResolveStopPlaces(planStopsData, filteredPlaces),
                message = $"Created a {prefs.Days}-day plan with {planStopsData.Count} stops!",
                warnings = schedule.Warnings
            });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { error = "Request cancelled" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Gemini API call failed during plan generation");
            return StatusCode(502, new { error = "AI service temporarily unavailable" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogError(ex, "Plan generation failed");
            return StatusCode(500, new { error = "Failed to generate plan" });
        }
    }

    // ── RAG retrieval (stays here: needs _db, _embeddings, _ranker) ──────────────

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

        var parts = new List<string> { rawMessage };
        if (prefs.Categories != null && prefs.Categories.Count > 0)
            parts.Add(string.Join(" ", prefs.Categories));
        if (prefs.Vibes != null && prefs.Vibes.Count > 0)
            parts.Add(string.Join(" ", prefs.Vibes));
        if (!string.IsNullOrWhiteSpace(prefs.GroupType))
            parts.Add(prefs.GroupType);
        var queryText = string.Join(". ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        Vector? qvec;
        try
        {
            qvec = await _embeddings.EmbedAsync(queryText, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Embedding provider failed — RAG fallback");
            return await FallbackKeywordFilterAsync(city, prefs, ct);
        }

        if (qvec == null)
        {
            _logger.LogWarning("EmbedAsync returned null — RAG fallback");
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
            _logger.LogInformation("RAG returned 0 candidates — city={City}, falling back", city);
            return await FallbackKeywordFilterAsync(city, prefs, ct);
        }

        var rankedScored = _ranker.RankWithScores(
            candidates.Select(c => (c.Place, c.Distance)).ToList(),
            prefs);

        _logger.LogInformation(
            "RAG retrieval: city={City} query='{QueryPreview}' top-K={Count} top5=[{Top5}]",
            city,
            queryText.Length > 60 ? queryText[..60] + "…" : queryText,
            rankedScored.Count,
            string.Join(" | ", rankedScored.Take(5).Select(s =>
                $"{s.Place.Name}({s.Place.Category}) total={s.Score:F3} " +
                $"[sim={s.Breakdown.Similarity:F2} cat={s.Breakdown.CategoryMatch:F2} " +
                $"bestFor={s.Breakdown.BestForMatch:F2} aiVibe={s.Breakdown.AiVibeNormalized:F2} " +
                $"neighPen={s.Breakdown.NeighborhoodPenalty:F2}]")));

        return rankedScored.Select(s => s.Place).ToList();
    }

    private const int FallbackKeywordHardCap = 500;

    private async Task<List<Place>> FallbackKeywordFilterAsync(
        string city, ExtractedPreferences prefs, CancellationToken ct)
    {
        var matching = await _db.Places.AsNoTracking()
            .Where(p => p.Status == "published" && p.City == city)
            .Take(FallbackKeywordHardCap)
            .ToListAsync(ct);
        return FilterPlaces(matching, prefs);
    }

    internal static List<Place> FilterPlaces(List<Place> allPlaces, ExtractedPreferences prefs)
    {
        if (prefs.Categories == null || !prefs.Categories.Any()) return allPlaces;

        return allPlaces.Where(p =>
            prefs.Categories.Any(c =>
                string.Equals(p.Category, c, StringComparison.OrdinalIgnoreCase)
                || p.Category.Contains(c, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // ── Plan naming — test contract surface (BuilderPlanNameTests.cs calls these directly) ──

    public static string BuildPlanName(ExtractedPreferences prefs, string city, string rawMessage)
        => PlanNamingService.BuildPlanName(prefs, city, rawMessage);

    public static string BuildPlanDescription(ExtractedPreferences prefs)
        => PlanNamingService.BuildPlanDescription(prefs);

    // ── Input validation ──────────────────────────────────────────────────────────

    private static (bool accepted, Dictionary<string, bool> signals) ValidateMinimumInput(BuilderChatRequest request)
    {
        var hasMessage = !string.IsNullOrWhiteSpace(request.Message);

        var city      = !string.IsNullOrWhiteSpace(request.TripContext?.City);
        var days      = request.TripContext?.Days.HasValue == true;
        var groupType = !string.IsNullOrWhiteSpace(request.TripContext?.GroupType);
        var interests = (request.TripContext?.Categories?.Count > 0)
                     || (request.TripContext?.Subcategories?.Count > 0);
        var budget    = !string.IsNullOrWhiteSpace(request.TripContext?.Budget);

        var wizardSignals = (city ? 1 : 0)
                          + (days ? 1 : 0)
                          + (groupType ? 1 : 0)
                          + (interests ? 1 : 0)
                          + (budget ? 1 : 0);

        var accepted = wizardSignals >= 3;

        return (accepted, new Dictionary<string, bool>
        {
            ["chat_message"]     = hasMessage,
            ["wizard_city"]      = city,
            ["wizard_days"]      = days,
            ["wizard_groupType"] = groupType,
            ["wizard_interests"] = interests,
            ["wizard_budget"]    = budget,
        });
    }
}
