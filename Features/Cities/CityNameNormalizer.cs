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
