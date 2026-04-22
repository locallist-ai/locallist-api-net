using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using LocalList.API.NET.Features.Builder.Services;
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

        try
        {
            // 1. Extract preferences from Gemini
            var prefs = await _aiProvider.ExtractPreferencesAsync(request.Message, request.TripContext, ct);

            // 2+3. Retrieve + rank candidates semánticamente (RAG) con fallback a filtrado por categoría.
            var city = request.TripContext?.City ?? "Miami"; // Default fallback
            var filteredPlaces = await RetrieveCandidatesAsync(request.Message, city, prefs, ct);

            // 4. Build schedule (Haversine scheduling algorithm)
            var planStopsData = BuildPlanSchedule(filteredPlaces, prefs);

            var sanitizedMessage = request.Message.Length > 60 ? request.Message[..60] : request.Message;
            var planName = string.IsNullOrEmpty(prefs.PlanName) ? $"{sanitizedMessage} Plan" : prefs.PlanName;

            if (isAnonymous)
            {
                var ephemeralPlan = new
                {
                    Id = Guid.NewGuid(),
                    Name = planName,
                    City = city,
                    Type = "ai",
                    Description = $"AI-generated plan: {sanitizedMessage}",
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
                    message = $"Created a {prefs.Days}-day plan with {planStopsData.Count} stops!"
                });
            }

            // Authenticated: Create Physical Database Entities
            var plan = new Plan
            {
                Name = planName,
                City = city,
                Type = "ai",
                Description = $"AI-generated plan: {sanitizedMessage}",
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
                message = $"Created a {prefs.Days}-day plan with {planStopsData.Count} stops!"
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

        var ranked = _ranker.Rank(
            candidates.Select(c => (c.Place, c.Distance)).ToList(),
            prefs);

        _logger.LogInformation(
            "RAG retrieval: city={City} query='{QueryPreview}' top-K={Count}",
            city,
            queryText.Length > 60 ? queryText[..60] + "…" : queryText,
            ranked.Count);

        return ranked;
    }

    /// <summary>
    /// Fallback legacy: query categórica simple (comportamiento previo a Fase 2 RAG).
    /// Se usa cuando el catálogo no está reindexado o el embedding provider falla.
    /// </summary>
    private async Task<List<Place>> FallbackKeywordFilterAsync(
        string city, ExtractedPreferences prefs, CancellationToken ct)
    {
        var matching = await _db.Places.AsNoTracking()
            .Where(p => p.Status == "published" && p.City == city)
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

    private List<ScheduledStopDto> BuildPlanSchedule(List<Place> filteredPlaces, ExtractedPreferences prefs)
    {
        var stops = new List<ScheduledStopDto>();
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

                var place = shuffled.FirstOrDefault(p =>
                    !usedPlaceIds.Contains(p.Id) &&
                    IsGoodTimeMatch(p, slot.TimeBlock));

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

        return stops;
    }

    private bool IsGoodTimeMatch(Place place, string timeBlock)
    {
        if (string.IsNullOrEmpty(place.BestTime) || place.BestTime.ToLower() == "any") return true;

        var dict = new Dictionary<string, string[]>
        {
            { "morning", new[] { "morning" } },
            { "lunch", new[] { "lunch", "morning", "afternoon" } },
            { "afternoon", new[] { "afternoon", "morning" } },
            { "dinner", new[] { "dinner", "evening", "lunch" } },
            { "evening", new[] { "evening" } }
        };

        if (dict.TryGetValue(timeBlock, out var matchingTimes))
        {
            return matchingTimes.Any(t => place.BestTime.ToLower().Contains(t));
        }

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
