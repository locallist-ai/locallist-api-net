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

    // ── Localizacion ES de los tokens canonicos del pipeline ────────────────────
    // Los valores que llegan a prefs.Vibes / prefs.Categories / prefs.GroupType son
    // tokens canonicos EN (AllowedGroupTypes de PreferenceExtractorService, vibes
    // del prompt/SlotExtractorService, PlaceTaxonomy.Categories en lowercase).
    // Interpolarlos verbatim en la frase ES producia mezclas ("Plan de 2 días de
    // romantic en Miami"). Estos diccionarios los traducen; REGLA: token desconocido
    // se OMITE con gracia (frase generica sin el) — nunca se interpola ingles dentro
    // de la frase espanola. Lookup case-insensitive: el casing no esta garantizado
    // (el parse del LLM valida con ToLower pero conserva el original).

    // Descriptor del NOMBRE: incluye su conector para integrarse en
    // "Plan de N días {descriptor} en {ciudad}" (adjetivo pospuesto o sintagma "de X").
    private static readonly Dictionary<string, string> EsNameDescriptors = new(StringComparer.OrdinalIgnoreCase)
    {
        // Vibes (set del prompt de extraccion + SlotExtractorService)
        ["romantic"]    = "romántico",
        ["adventurous"] = "de aventura",
        ["relaxed"]     = "relajado",
        ["party"]       = "de fiesta",
        ["cultural"]    = "cultural",
        ["foodie"]      = "gastronómico",
        ["hidden_gems"] = "de rincones ocultos",
        ["family"]      = "familiar",
        // Categorias (PlaceTaxonomy.Categories) — el keyword-fallback del extractor
        // copia categorias dentro de Vibes, asi que tambien deben resolverse aqui.
        ["food"]        = "de gastronomía",
        ["nightlife"]   = "de vida nocturna",
        ["coffee"]      = "de cafés",
        ["outdoors"]    = "al aire libre",
        ["wellness"]    = "de bienestar",
        ["culture"]     = "de cultura",
        ["shopping"]    = "de compras",
    };

    // Sustantivos de categoria para la lista "con X, Y, Z" de la DESCRIPCION.
    private static readonly Dictionary<string, string> EsCategoryNouns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["food"]      = "gastronomía",
        ["nightlife"] = "vida nocturna",
        ["coffee"]    = "cafés",
        ["outdoors"]  = "aire libre",
        ["wellness"]  = "bienestar",
        ["culture"]   = "cultura",
        ["shopping"]  = "compras",
    };

    // GroupType (set cerrado AllowedGroupTypes) como sintagma integrado:
    // "Un plan de 2 días en pareja con ...".
    private static readonly Dictionary<string, string> EsGroupPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["solo"]        = "en solitario",
        ["couple"]      = "en pareja",
        ["friends"]     = "con amigos",
        ["family-kids"] = "en familia con niños",
        ["family"]      = "en familia",
        ["group"]       = "en grupo",
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

        var cityLabel = string.IsNullOrWhiteSpace(city) ? "Miami" : city;

        if (lang == "es")
        {
            var dayLabel = prefs.Days == 1 ? "1 día" : $"{prefs.Days} días";
            // Primer vibe traducible; si ninguno, primera categoria traducible.
            // Token desconocido => se omite (frase generica), nunca ingles verbatim.
            var descriptorEs = FirstTranslatable(prefs.Vibes, EsNameDescriptors)
                ?? FirstTranslatable(prefs.Categories, EsNameDescriptors);
            return descriptorEs is null
                ? $"Plan a medida de {dayLabel} en {cityLabel}"
                : $"Plan de {dayLabel} {descriptorEs} en {cityLabel}";
        }

        // Camino EN: byte-identico al comportamiento previo.
        var descriptor = FirstNonEmpty(prefs.Vibes) ?? FirstNonEmpty(prefs.Categories) ?? "curated";
        var dayLabelEn = prefs.Days == 1 ? "1-day" : $"{prefs.Days}-day";
        return $"{dayLabelEn} {descriptor} plan in {cityLabel}";
    }

    public static string BuildPlanDescription(ExtractedPreferences prefs, string lang = "en")
    {
        var topCats = (prefs.Categories ?? new List<string>()).Take(3).ToList();

        if (lang == "es")
        {
            var dayLabel = prefs.Days == 1 ? "1 día" : $"{prefs.Days} días";
            // GroupType fuera del set cerrado => se omite la clausula (sin ingles verbatim).
            var groupClause = !string.IsNullOrWhiteSpace(prefs.GroupType)
                && EsGroupPhrases.TryGetValue(prefs.GroupType.Trim(), out var groupEs)
                    ? $" {groupEs}"
                    : "";
            // Se traducen las top-3; las no mapeadas se descartan de la lista.
            var catsEs = topCats
                .Select(c => !string.IsNullOrWhiteSpace(c) && EsCategoryNouns.TryGetValue(c.Trim(), out var noun) ? noun : null)
                .OfType<string>()
                .ToList();
            return catsEs.Count == 0
                ? $"Un plan de {dayLabel}{groupClause}."
                : $"Un plan de {dayLabel}{groupClause} con {string.Join(", ", catsEs)}.";
        }

        var dayLabelEn = prefs.Days == 1 ? "1-day" : $"{prefs.Days}-day";
        var groupLabel = string.IsNullOrWhiteSpace(prefs.GroupType) ? "curated" : $"{prefs.GroupType}-friendly";
        if (topCats.Count == 0)
            return $"A {groupLabel} {dayLabelEn} plan.";
        return $"A {groupLabel} {dayLabelEn} plan featuring {string.Join(", ", topCats)}.";
    }

    private static string? FirstTranslatable(IEnumerable<string>? tokens, IReadOnlyDictionary<string, string> map)
    {
        if (tokens == null) return null;
        foreach (var token in tokens)
        {
            if (!string.IsNullOrWhiteSpace(token) && map.TryGetValue(token.Trim(), out var es))
                return es;
        }
        return null;
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
