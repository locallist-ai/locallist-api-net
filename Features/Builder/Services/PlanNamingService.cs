using LocalList.API.NET.Shared.Dtos;

namespace LocalList.API.NET.Features.Builder.Services;

public static class PlanNamingService
{
    internal static readonly string[] GreetingPrefixes =
    {
        "hola", "hi", "hey", "hello", "buenas", "buenos dias", "buenos días", "good morning",
        "saludos", "holi"
    };

    private static readonly string[] DefaultPlaceholderNames =
    {
        "my plan", "new plan", "untitled", "plan", "trip", "trip plan", "your plan"
    };

    public static string BuildPlanName(ExtractedPreferences prefs, string city, string rawMessage)
    {
        var candidate = prefs.PlanName?.Trim() ?? string.Empty;
        var raw = rawMessage?.Trim() ?? string.Empty;

        if (IsUsableName(candidate, raw))
            return candidate;

        var descriptor = FirstNonEmpty(prefs.Vibes) ?? FirstNonEmpty(prefs.Categories) ?? "curated";
        var dayLabel = prefs.Days == 1 ? "1-day" : $"{prefs.Days}-day";
        var cityLabel = string.IsNullOrWhiteSpace(city) ? "Miami" : city;
        return $"{dayLabel} {descriptor} plan in {cityLabel}";
    }

    public static string BuildPlanDescription(ExtractedPreferences prefs)
    {
        var dayLabel = prefs.Days == 1 ? "1-day" : $"{prefs.Days}-day";
        var groupLabel = string.IsNullOrWhiteSpace(prefs.GroupType) ? "curated" : $"{prefs.GroupType}-friendly";
        var topCats = (prefs.Categories ?? new List<string>()).Take(3).ToList();
        if (topCats.Count == 0)
            return $"A {groupLabel} {dayLabel} plan.";
        return $"A {groupLabel} {dayLabel} plan featuring {string.Join(", ", topCats)}.";
    }

    private static bool IsUsableName(string candidate, string rawMessage)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (candidate.Length < 4) return false;

        var lower = candidate.ToLowerInvariant().Trim();

        if (DefaultPlaceholderNames.Contains(lower)) return false;
        if (GreetingPrefixes.Any(g => lower.StartsWith(g))) return false;

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
}
