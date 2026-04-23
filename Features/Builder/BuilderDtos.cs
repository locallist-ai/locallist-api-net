using System.ComponentModel.DataAnnotations;

namespace LocalList.API.NET.Features.Builder;

public class BuilderChatRequest
{
    // Message opcional (Pablo 2026-04-23): el chat complementa al wizard pero no lo sustituye.
    // Si el user no escribe nada, el wizard debe tener mínimo 3/5 señales para generar plan.
    // Si el user escribe algo, se pasa tal cual al pipeline (Gemini + embedding query).
    [MaxLength(5000)]
    public string? Message { get; set; }
    public TripContextDto? TripContext { get; set; }
}

public class ExtractedPreferences
{
    public int Days { get; set; } = 1;
    public List<string> Categories { get; set; } = new();
    public List<string> Vibes { get; set; } = new();
    public string GroupType { get; set; } = "couple";
    public string PlanName { get; set; } = "My Plan";
    public int MaxStopsPerDay { get; set; } = 5;
}

public class TripContextDto
{
    [MaxLength(20)]
    public string? GroupType { get; set; }
    [MaxLength(10)]
    public List<string>? Preferences { get; set; }
    [MaxLength(10)]
    public List<string>? Vibes { get; set; }
    [Range(1, 7)]
    public int? Days { get; set; }
    [MaxLength(100)]
    public string? City { get; set; }
    [MaxLength(20)]
    public string? Budget { get; set; } // "budget" | "moderate" | "premium"
}
