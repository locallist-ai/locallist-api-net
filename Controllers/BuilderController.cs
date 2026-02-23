using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;
using LocalList.API.NET.Data;
using LocalList.API.NET.Data.Models;
using LocalList.API.NET.Services;

namespace LocalList.API.NET.Controllers;

[ApiController]
[Route("builder")]
public class BuilderController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly AiProviderService _aiProvider;

    public BuilderController(LocalListDbContext db, AiProviderService aiProvider)
    {
        _db = db;
        _aiProvider = aiProvider;
    }

    [HttpPost("chat")]
    [AllowAnonymous]
    [EnableRateLimiting("BuilderLimit")]
    public async Task<IActionResult> GeneratePlan([FromBody] BuilderChatRequest request)
    {
        var isAnonymous = !User.Identity?.IsAuthenticated ?? true;
        
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid? userId = string.IsNullOrEmpty(userIdString) ? null : Guid.Parse(userIdString);

        try
        {
            // 1. Extract preferences from Gemini
            var prefs = await _aiProvider.ExtractPreferencesAsync(request.Message, request.TripContext);

            // 2. Query Curated Places matching the City
            var city = request.TripContext?.City ?? "Miami"; // Default fallback
            var matchingPlaces = await _db.Places
                .Where(p => p.Status == "published" && p.City == city)
                .ToListAsync();

            // 3. Filter using Category preferences
            var filteredPlaces = FilterPlaces(matchingPlaces, prefs);

            // 4. Build schedule (Haversine scheduling algorithm)
            var planStopsData = BuildPlanSchedule(filteredPlaces, prefs);

            var planName = string.IsNullOrEmpty(prefs.PlanName) ? $"{request.Message.Substring(0, Math.Min(60, request.Message.Length))} Plan" : prefs.PlanName;

            if (isAnonymous)
            {
                var ephemeralPlan = new
                {
                    Id = Guid.NewGuid(),
                    Name = planName,
                    City = city,
                    Type = "ai",
                    Description = $"AI-generated plan: {request.Message}",
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
                Description = $"AI-generated plan: {request.Message}",
                DurationDays = prefs.Days,
                TripContext = request.TripContext != null ? JsonDocument.Parse(JsonSerializer.Serialize(request.TripContext)) : JsonDocument.Parse("{}"),
                IsPublic = false,
                CreatedById = userId
            };

            _db.Plans.Add(plan);
            await _db.SaveChangesAsync();

            var stopsToInsert = planStopsData.Select(sd => new PlanStop
            {
                PlanId = plan.Id,
                PlaceId = sd.PlaceId,
                DayNumber = sd.DayNumber,
                OrderIndex = sd.OrderIndex,
                TimeBlock = sd.TimeBlock,
                SuggestedArrival = string.IsNullOrEmpty(sd.SuggestedArrival) ? null : TimeSpan.Parse(sd.SuggestedArrival),
                SuggestedDurationMin = sd.SuggestedDurationMin,
                TravelFromPrevious = sd.TravelFromPrevious != null ? JsonDocument.Parse(JsonSerializer.Serialize(sd.TravelFromPrevious)) : null
            }).ToList();

            if (stopsToInsert.Any())
            {
                _db.PlanStops.AddRange(stopsToInsert);
                await _db.SaveChangesAsync();
            }

            var stopsWithPlaces = ResolveStopPlaces(planStopsData, filteredPlaces);

            return Ok(new
            {
                plan,
                stops = stopsWithPlaces,
                message = $"Created a {prefs.Days}-day plan with {planStopsData.Count} stops!"
            });

        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to generate plan", details = ex.Message });
        }
    }

    private List<Place> FilterPlaces(List<Place> allPlaces, ExtractedPreferences prefs)
    {
        if (!prefs.Categories.Any()) return allPlaces;

        var catMap = new Dictionary<string, string>
        {
            { "food", "food" },
            { "nightlife", "nightlife" } ,
            { "coffee", "coffee" } ,
            { "outdoors", "outdoors" },
            { "wellness", "wellness" },
            { "culture", "culture" }
        };

        return allPlaces.Where(p => 
        {
            var pCat = p.Category.ToLower();
            return prefs.Categories.Any(c => 
                (catMap.ContainsKey(c) && catMap[c] == pCat) || pCat.Contains(c));
        }).ToList();
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
        
        var random = new Random();
        var shuffled = filteredPlaces.OrderBy(x => random.Next()).ToList();

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

public class BuilderChatRequest
{
    public required string Message { get; set; }
    public TripContextDto? TripContext { get; set; }
}
