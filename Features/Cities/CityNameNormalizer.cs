using System.Globalization;
using System.Text;

namespace LocalList.API.NET.Features.Cities;

/// <summary>
/// Normaliza nombres de ciudad a una representación canónica (lowercase ASCII
/// sin acentos, sin control chars). Pablo 2026-04-27 — vive aparte del
/// controller para que tests + futuros consumidores (importers, seeders) no
/// acoplen al controller.
///
/// "Málaga" → "malaga", "São Paulo" → "sao paulo", "MIAMI" → "miami".
/// </summary>
public static class CityNameNormalizer
{
    /// <summary>Máxima longitud aceptada para `q` en search y `name` en create.</summary>
    public const int MaxRawLength = 64;

    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        // Strip control + format chars BEFORE normalization. Zero-width joiners
        // y similares (Cf) no aportan a un nombre legítimo y abren homoglyph
        // variants en el unique index.
        var trimmed = raw.Trim();
        var formD = trimmed.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (category == UnicodeCategory.Control) continue;
            if (category == UnicodeCategory.Format) continue;
            sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}

/// <summary>
/// Heurísticas mínimas para rechazar inputs basura en POST /cities.
/// Pablo 2026-04-29 — el builder custom dejaba pasar "Mal", "abc", "asdf".
///
/// Opera sobre el nombre ya normalizado (resultado de <see cref="CityNameNormalizer.Normalize"/>):
///   - longitud >= 3
///   - contiene al menos una vocal ASCII (a/e/i/o/u)
///   - no está en la blocklist de tokens no-lugar conocidos
/// </summary>
public static class CityNameValidator
{
    public static bool IsLikelyRealCity(string normalized, out string? reason)
    {
        if (normalized.Length < 3) { reason = "name must be at least 3 characters"; return false; }
        if (!normalized.Any(c => "aeiou".Contains(c))) { reason = "name must contain a vowel"; return false; }
        if (Blocklist.Contains(normalized)) { reason = "name is not a recognized city"; return false; }
        reason = null;
        return true;
    }

    private static readonly HashSet<string> Blocklist = new(StringComparer.Ordinal)
    {
        "mal", "abc", "xyz", "asdf", "asd", "qwe", "qwerty", "test", "demo",
        "foo", "bar", "baz", "lorem", "ipsum", "null", "none", "todo",
    };
}
