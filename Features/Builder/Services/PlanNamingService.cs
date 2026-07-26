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

    // Fallback bilingue (EN/ES-Espana) seleccionado por el `lang` de la request.
    // Motivo: el nombre/descripcion de fallback se persiste bajo NameI18n[lang]
    // (ChatController / BuilderController). Un template siempre-EN etiquetaba texto
    // ingles como espanol. El texto de Gemini ya respeta el idioma; esto solo cubre
    // el camino de rechazo (IsUsableName) o ausencia del nombre del LLM.
    public static string BuildPlanName(ExtractedPreferences prefs, string city, string rawMessage, string lang = "en")
    {
        var candidate = prefs.PlanName?.Trim() ?? string.Empty;
        var raw = rawMessage?.Trim() ?? string.Empty;

        if (IsUsableName(candidate, raw))
            return candidate;

        // descriptor = vibe/categoria extraida (dato pasante, no traducible aqui).
        var descriptor = FirstNonEmpty(prefs.Vibes) ?? FirstNonEmpty(prefs.Categories);
        var cityLabel = string.IsNullOrWhiteSpace(city) ? "Miami" : city;

        if (lang == "es")
        {
            var dayLabel = prefs.Days == 1 ? "1 día" : $"{prefs.Days} días";
            return descriptor is null
                ? $"Plan a medida de {dayLabel} en {cityLabel}"
                : $"Plan de {dayLabel} de {descriptor} en {cityLabel}";
        }

        var dayLabelEn = prefs.Days == 1 ? "1-day" : $"{prefs.Days}-day";
        return $"{dayLabelEn} {descriptor ?? "curated"} plan in {cityLabel}";
    }

    public static string BuildPlanDescription(ExtractedPreferences prefs, string lang = "en")
    {
        var topCats = (prefs.Categories ?? new List<string>()).Take(3).ToList();

        if (lang == "es")
        {
            var dayLabel = prefs.Days == 1 ? "1 día" : $"{prefs.Days} días";
            var groupClause = string.IsNullOrWhiteSpace(prefs.GroupType) ? "" : $" ideal para {prefs.GroupType}";
            return topCats.Count == 0
                ? $"Un plan de {dayLabel}{groupClause}."
                : $"Un plan de {dayLabel}{groupClause} con {string.Join(", ", topCats)}.";
        }

        var dayLabelEn = prefs.Days == 1 ? "1-day" : $"{prefs.Days}-day";
        var groupLabel = string.IsNullOrWhiteSpace(prefs.GroupType) ? "curated" : $"{prefs.GroupType}-friendly";
        if (topCats.Count == 0)
            return $"A {groupLabel} {dayLabelEn} plan.";
        return $"A {groupLabel} {dayLabelEn} plan featuring {string.Join(", ", topCats)}.";
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
