using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Features.Builder.Shared;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
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
    private readonly ILogger<BuilderController> _logger;

    // Mínimo de places con embedding para considerar activo el retrieval semántico.
    // Por debajo de este umbral → fallback al filtrado por categoría (comportamiento legacy).
    private const int MinEmbeddedPlacesForRag = 3;

    // Top-K devuelto por el índice HNSW antes del rerank. BuildPlanSchedule consume
    // ~15, así que 50 deja margen al rerank para reordenar sin perder buenos candidatos.
    private const int RetrievalTopK = 50;

    public BuilderController(
        LocalListDbContext db,
        AiProviderService aiProvider,
        EmbeddingService embeddings,
        PlaceRankingService ranker,
        ILogger<BuilderController> logger)
    {
        _db = db;
        _aiProvider = aiProvider;
        _embeddings = embeddings;
        _ranker = ranker;
        _logger = logger;
    }

    [HttpPost("chat")]
    [AllowAnonymous]
    [EnableRateLimiting("BuilderLimit")]
    public async Task<IActionResult> GeneratePlan([FromBody] BuilderChatRequest request, CancellationToken ct)
    {
        var isAnonymous = !User.Identity?.IsAuthenticated ?? true;

        Guid? userId = isAnonymous ? null : await User.GetUserIdAsync(_db, ct);

        // Structured transcript of the Builder pipeline — every stage logs at Info
        // so a single Railway Deploy Logs query (filter "Builder:") returns the full
        // story of any /builder/chat request without code changes.
        _logger.LogInformation(
            "Builder: request anonymous={IsAnon} city={City} days={Days} groupType={GroupType} preferences={Prefs} vibes={Vibes} msgLen={MsgLen}",
            isAnonymous,
            request.TripContext?.City ?? "(unset)",
            request.TripContext?.Days?.ToString() ?? "(unset)",
            request.TripContext?.GroupType ?? "(unset)",
            request.TripContext?.Preferences == null ? "(null)" : string.Join(",", request.TripContext.Preferences),
            request.TripContext?.Vibes == null ? "(null)" : string.Join(",", request.TripContext.Vibes),
            request.Message.Length);

        // Require minimum useful input — descriptive message OR ≥2 wizard signals.
        // Sin esto, el endpoint acepta {"message":"x"} y genera planes ruidosos con defaults.
        var inputCheck = ValidateMinimumInput(request);
        if (!inputCheck.accepted)
        {
            _logger.LogInformation(
                "Builder: rejected insufficient_input — msgDescriptive={M} wizardDays={D} wizardGroup={G} wizardPrefs={P}",
                inputCheck.signals["message_descriptive"],
                inputCheck.signals["wizard_days"],
                inputCheck.signals["wizard_groupType"],
                inputCheck.signals["wizard_preferences"]);
            return BadRequest(new
            {
                error = "insufficient_input",
                message = "Please complete at least 2 wizard steps (duration, group, preferences) or describe your trip in 20+ characters.",
                signals = inputCheck.signals
            });
        }

        try
        {
            // 1. Extract preferences from Gemini
            var prefs = await _aiProvider.ExtractPreferencesAsync(request.Message, request.TripContext, ct);

            _logger.LogInformation(
                "Builder: prefs days={Days} categories={Cats} vibes={Vibes} groupType={GT} planName='{Name}' maxStopsPerDay={Max}",
                prefs.Days,
                prefs.Categories == null ? "(null)" : string.Join(",", prefs.Categories),
                prefs.Vibes == null ? "(null)" : string.Join(",", prefs.Vibes),
                prefs.GroupType,
                prefs.PlanName,
                prefs.MaxStopsPerDay);

            // 2+3. Retrieve + rank candidates semánticamente (RAG) con fallback a filtrado por categoría.
            var city = request.TripContext?.City ?? "Miami"; // Default fallback
            var filteredPlaces = await RetrieveCandidatesAsync(request.Message, city, prefs, ct);

            _logger.LogInformation("Builder: candidates count={Count}", filteredPlaces.Count);

            // 4. Build schedule (Haversine scheduling algorithm)
            var schedule = BuildPlanSchedule(filteredPlaces, prefs);
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

            var sanitizedMessage = request.Message.Length > 60 ? request.Message[..60] : request.Message;
            var planName = BuildPlanName(prefs, city, request.Message);
            var planDescription = BuildPlanDescription(prefs);

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

                var stopsWithPlacesAnonymous = ResolveStopPlaces(planStopsData, filteredPlaces);

                return Ok(new
                {
                    plan = ephemeralPlan,
                    stops = stopsWithPlacesAnonymous,
                    message = $"Created a {prefs.Days}-day plan with {planStopsData.Count} stops!",
                    warnings = schedule.Warnings
                });
            }

            // Authenticated: Create Physical Database Entities
            var plan = new Plan
            {
                Name = planName,
                City = city,
                Type = "ai",
                Description = planDescription,
                DurationDays = prefs.Days,
                TripContext = request.TripContext != null ? JsonSerializer.SerializeToDocument(request.TripContext) : JsonSerializer.SerializeToDocument(new {}),
                IsPublic = false,
                CreatedById = userId
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
            {
                _db.PlanStops.AddRange(stopsToInsert);
            }

            // Persist plan + stops en un único roundtrip (EF resuelve FKs por Guid client-side).
            await _db.SaveChangesAsync(ct);

            var stopsWithPlaces = ResolveStopPlaces(planStopsData, filteredPlaces);

            return Ok(new
            {
                plan,
                stops = stopsWithPlaces,
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

    /// <summary>
    /// RAG retrieval con fallback: embeddea el query del usuario, saca top-K del catálogo
    /// por cosine distance contra el índice HNSW, y rerank con señales determinísticas.
    /// Si hay menos de <see cref="MinEmbeddedPlacesForRag"/> places con embedding en esa city
    /// o si el proveedor de embeddings falla, cae al filtrado por categoría legacy.
    /// </summary>
    private async Task<List<Place>> RetrieveCandidatesAsync(
        string rawMessage, string city, ExtractedPreferences prefs, CancellationToken ct)
    {
        // Contar places con embedding para esta city. AsNoTracking porque no muta.
        var embeddedCount = await _db.Places.AsNoTracking()
            .CountAsync(p => p.Status == "published" && p.City == city && p.Embedding != null, ct);

        if (embeddedCount < MinEmbeddedPlacesForRag)
        {
            _logger.LogInformation(
                "RAG fallback (keyword): city={City} embeddedCount={Count} < {Min}",
                city, embeddedCount, MinEmbeddedPlacesForRag);
            return await FallbackKeywordFilterAsync(city, prefs, ct);
        }

        // Componer query text combinando mensaje + preferencias extraídas.
        // Filtramos partes vacías para no ensuciar el embedding con ". . . ".
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

        // Top-K por cosine distance via índice HNSW. Proyección incluye la distance
        // (en [0..2], 0 = idéntico) para pasar al ranker.
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

    /// <summary>
    /// Fallback legacy: query categórica simple (comportamiento previo a Fase 2 RAG).
    /// Se usa cuando el catálogo no está reindexado o el embedding provider falla.
    /// </summary>
    // Hard cap del fallback keyword: aunque la city tenga miles de places, BuildPlanSchedule
    // consume ~5-15 stops en total. 500 es buffer generoso para el filter categoría
    // posterior sin tirar toda la city a memoria (audit SRE 2026-04-22).
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

    private class ScheduledStopDto
    {
        public Guid PlaceId { get; set; }
        public int DayNumber { get; set; }
        public int OrderIndex { get; set; }
        public string TimeBlock { get; set; } = string.Empty;
        public string? SuggestedArrival { get; set; }
        public int SuggestedDurationMin { get; set; }
        public TravelInfoDto? TravelFromPrevious { get; set; }
    }

    private class TravelInfoDto
    {
        public double distance_km { get; set; }
        public int duration_min { get; set; }
        public string mode { get; set; } = "drive";
    }

    /// <summary>
    /// Resultado de <see cref="BuildPlanSchedule"/>: stops + metadata de qué reglas
    /// tuvimos que relajar. Los warnings se propagan al response para que el cliente
    /// pueda avisar al usuario ("este plan se construyó con catálogo limitado", etc).
    /// </summary>
    private sealed class ScheduleResult
    {
        public List<ScheduledStopDto> Stops { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    private ScheduleResult BuildPlanSchedule(List<Place> filteredPlaces, ExtractedPreferences prefs)
    {
        var result = new ScheduleResult();
        var stops = result.Stops;
        var usedPlaceIds = new HashSet<Guid>();

        var shuffled = filteredPlaces.OrderBy(x => Random.Shared.Next()).ToList();

        var dayTemplate = new[]
        {
            new { TimeBlock = "morning", Arrival = "09:00", Duration = 60 },
            new { TimeBlock = "lunch", Arrival = "12:00", Duration = 90 },
            new { TimeBlock = "afternoon", Arrival = "14:30", Duration = 90 },
            new { TimeBlock = "dinner", Arrival = "19:00", Duration = 90 },
            new { TimeBlock = "evening", Arrival = "21:00", Duration = 60 }
        };

        for (int day = 1; day <= prefs.Days; day++)
        {
            var daySlots = dayTemplate.Take(prefs.MaxStopsPerDay).ToList();
            double? prevLat = null;
            double? prevLon = null;

            for (int i = 0; i < daySlots.Count; i++)
            {
                var slot = daySlots[i];

                // Primera pasada: filtro fuerte (BestTime ∩ matrix categoría×timeBlock ∩ exclusión family-nightlife).
                var strictEligible = shuffled.Where(p =>
                    !usedPlaceIds.Contains(p.Id) &&
                    IsGoodTimeMatch(p, slot.TimeBlock, prefs, strict: true)).ToList();

                // Soft fallback: si el filtro estricto dejó 0 elegibles, caemos a sólo BestTime.
                // Preservamos el logging para ver cuándo pasa en prod.
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

    // Matrix categoría × timeBlock: qué categorías tienen sentido en qué slot del día.
    // morning: desayunos y actividades que abren temprano.
    // lunch: comidas del mediodía (principalmente food, algún café que sirva almuerzo).
    // afternoon: actividades al aire libre, cultura, cafés.
    // dinner: cenas. 'wellness' sale porque a esas horas raramente tiene sentido.
    // evening: nightlife, cenas tardías, música/cultura nocturna.
    private static readonly Dictionary<string, HashSet<string>> TimeBlockCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["morning"] = new(StringComparer.OrdinalIgnoreCase) { "coffee", "wellness", "outdoors", "culture", "food" },
        ["lunch"] = new(StringComparer.OrdinalIgnoreCase) { "food", "coffee" },
        ["afternoon"] = new(StringComparer.OrdinalIgnoreCase) { "coffee", "outdoors", "culture", "food" },
        ["dinner"] = new(StringComparer.OrdinalIgnoreCase) { "food" },
        ["evening"] = new(StringComparer.OrdinalIgnoreCase) { "nightlife", "food", "culture" },
    };

    // BestTime del catálogo es texto libre ("morning", "any", "evening,night"); match por contains.
    private static readonly Dictionary<string, string[]> BestTimeMatches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["morning"] = new[] { "morning" },
        ["lunch"] = new[] { "lunch", "morning", "afternoon" },
        ["afternoon"] = new[] { "afternoon", "morning" },
        ["dinner"] = new[] { "dinner", "evening", "lunch" },
        ["evening"] = new[] { "evening" },
    };

    /// <summary>
    /// Filtro combinado para el scheduling. En modo <c>strict</c> aplica 3 reglas:
    ///   1. Family/family-kids NO admite <c>nightlife</c> en ningún timeBlock.
    ///   2. Category debe pertenecer al set de timeBlock (<c>TimeBlockCategories</c>).
    ///   3. BestTime debe encajar con el timeBlock (<c>BestTimeMatches</c>) — regla legacy.
    ///
    /// En modo <c>strict=false</c> aplica solo la regla 3 (BestTime). Sirve de soft fallback
    /// cuando el catálogo no tiene suficientes places para cubrir la intersección estricta.
    /// </summary>
    private bool IsGoodTimeMatch(Place place, string timeBlock, ExtractedPreferences prefs, bool strict)
    {
        if (strict)
        {
            // Regla 1: family/family-kids sin nightlife.
            if (GroupTypePolicy.IsFamilyContext(prefs.GroupType) &&
                string.Equals(place.Category, "nightlife", StringComparison.OrdinalIgnoreCase))
                return false;

            // Regla 2: matrix categoría × timeBlock. Si no tenemos entrada para el timeBlock
            // (no debería pasar con los 5 slots), caemos a permitir todas.
            if (TimeBlockCategories.TryGetValue(timeBlock, out var allowedCategories))
            {
                if (!allowedCategories.Contains(place.Category))
                    return false;
            }
        }

        // Regla 3 (siempre): BestTime. 'any' o vacío pasa siempre.
        if (string.IsNullOrEmpty(place.BestTime) || place.BestTime.ToLower() == "any") return true;
        if (BestTimeMatches.TryGetValue(timeBlock, out var matchingTimes))
            return matchingTimes.Any(t => place.BestTime.ToLower().Contains(t));

        return true;
    }

    private double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371; // Earth's radius in kilometers
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private double ToRad(double degrees)
    {
        return degrees * (Math.PI / 180);
    }

    private int EstimateTravelTime(double distanceKm, string mode)
    {
        var speedKmH = mode == "walk" ? 5.0 : 30.0;
        var timeHours = distanceKm / speedKmH;
        return (int)Math.Max(5, Math.Round(timeHours * 60)); // Minimum 5 mins
    }

    // ── Plan naming helpers (pure functions, internal para tests) ───────────────────────

    private static readonly string[] GreetingPrefixes =
    {
        "hola", "hi", "hey", "hello", "buenas", "buenos dias", "buenos días", "good morning",
        "saludos", "holi"
    };

    /// <summary>
    /// Genera un plan name descriptivo a partir de preferencias + ciudad. Ignora el mensaje
    /// crudo cuando es un saludo corto o cuando Gemini lo copió literal en <c>PlanName</c>.
    /// Pura — testeable sin DI.
    /// </summary>
    public static string BuildPlanName(ExtractedPreferences prefs, string city, string rawMessage)
    {
        var candidate = prefs.PlanName?.Trim() ?? string.Empty;
        var raw = rawMessage?.Trim() ?? string.Empty;

        if (IsUsableName(candidate, raw))
            return candidate;

        // Synthesize: "{Days}-day {vibe-or-category} in {City}".
        var descriptor = FirstNonEmpty(prefs.Vibes) ?? FirstNonEmpty(prefs.Categories) ?? "curated";
        var dayLabel = prefs.Days == 1 ? "1-day" : $"{prefs.Days}-day";
        var cityLabel = string.IsNullOrWhiteSpace(city) ? "Miami" : city;
        return $"{dayLabel} {descriptor} plan in {cityLabel}";
    }

    /// <summary>
    /// Description sustancial basada en groupType + duración + top-3 categorías.
    /// Evita el "AI-generated plan: Hola" que salía cuando el mensaje era trivial.
    /// </summary>
    public static string BuildPlanDescription(ExtractedPreferences prefs)
    {
        var dayLabel = prefs.Days == 1 ? "1-day" : $"{prefs.Days}-day";
        var groupLabel = string.IsNullOrWhiteSpace(prefs.GroupType) ? "curated" : $"{prefs.GroupType}-friendly";
        var topCats = (prefs.Categories ?? new List<string>()).Take(3).ToList();
        if (topCats.Count == 0)
            return $"A {groupLabel} {dayLabel} plan.";
        return $"A {groupLabel} {dayLabel} plan featuring {string.Join(", ", topCats)}.";
    }

    // Defaults/placeholder names que Gemini devuelve cuando ignora el prompt MANDATORY.
    // Detectados en prod 2026-04-23 — el fix context-wins no corrige el planName aquí,
    // hay que rechazarlos explícitamente para que BuildPlanName sintetice uno descriptivo.
    private static readonly string[] DefaultPlaceholderNames =
    {
        "my plan", "new plan", "untitled", "plan", "trip", "trip plan", "your plan"
    };

    private static bool IsUsableName(string candidate, string rawMessage)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (candidate.Length < 4) return false;

        var lower = candidate.ToLowerInvariant().Trim();

        // Placeholder/default names (ExtractedPreferences.PlanName default = "My Plan").
        if (DefaultPlaceholderNames.Contains(lower)) return false;

        // Greetings copiados tal cual.
        if (GreetingPrefixes.Any(g => lower.StartsWith(g))) return false;

        // PlanName contiene el mensaje literal del usuario (Gemini copy-paste).
        if (!string.IsNullOrWhiteSpace(rawMessage) && rawMessage.Length >= 4 &&
            lower.Contains(rawMessage.ToLowerInvariant()))
            return false;

        return true;
    }

    private static string? FirstNonEmpty(IEnumerable<string>? values)
    {
        if (values == null) return null;
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    // ── Input validation (Pablo feedback 2026-04-23) ───────────────────────────

    private const int DescriptiveMessageMinChars = 20;

    // Default placeholders comunes que el cliente puede enviar (i18n, autocomplete) y que
    // NO deben considerarse input descriptivo. Lowercase match.
    private static readonly HashSet<string> MessagePlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "x", "plan", "a plan", "create a plan", "make me a plan", "make a plan",
        "crea un plan", "hazme un plan", "un plan", "quiero un plan"
    };

    /// <summary>
    /// Valida que el request tenga input mínimo significativo. Acepta si cumple AL MENOS
    /// uno de: (A) mensaje descriptivo ≥20 chars no-placeholder, (B) ≥2 señales del wizard
    /// de entre {days, groupType válido, preferences||vibes no vacío}.
    /// </summary>
    private static (bool accepted, Dictionary<string, bool> signals) ValidateMinimumInput(BuilderChatRequest request)
    {
        var msgDescriptive = IsDescriptiveMessage(request.Message);

        var days = request.TripContext?.Days.HasValue == true;
        var groupType = !string.IsNullOrWhiteSpace(request.TripContext?.GroupType);
        var preferences = (request.TripContext?.Preferences?.Count > 0)
                       || (request.TripContext?.Vibes?.Count > 0);

        var wizardSignals = (days ? 1 : 0) + (groupType ? 1 : 0) + (preferences ? 1 : 0);
        var accepted = msgDescriptive || wizardSignals >= 2;

        return (accepted, new Dictionary<string, bool>
        {
            ["message_descriptive"] = msgDescriptive,
            ["wizard_days"] = days,
            ["wizard_groupType"] = groupType,
            ["wizard_preferences"] = preferences,
        });
    }

    private static bool IsDescriptiveMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var trimmed = message.Trim();
        if (trimmed.Length < DescriptiveMessageMinChars) return false;

        var lower = trimmed.ToLowerInvariant();

        // Greetings explícitos aunque tengan >20 chars (ej "hola hola hola hola hola hola").
        if (GreetingPrefixes.Any(g => lower.StartsWith(g))) return false;

        // Placeholders conocidos (el cliente puede enviar un default del i18n).
        if (MessagePlaceholders.Contains(lower)) return false;

        return true;
    }

    private object ResolveStopPlaces(List<ScheduledStopDto> stops, List<Place> allPlaces)
    {
        var placeMap = allPlaces.ToDictionary(p => p.Id);

        return stops.Select(stop =>
        {
            placeMap.TryGetValue(stop.PlaceId, out var place);
            return new
            {
                id = Guid.NewGuid(), // Ephemeral Stop ID for rendering
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
}
