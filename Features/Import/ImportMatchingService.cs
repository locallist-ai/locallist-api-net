using System.Text;
using LocalList.API.NET.Features.Cities;
using LocalList.API.NET.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.NET.Features.Import;

/// <summary>
/// Un candidato extraído del vídeo (T1/T2) enriquecido con el resultado del matching contra el
/// catálogo curado (F2 T3). <see cref="MatchedPlaceId"/> null = el sitio NO está en nuestro
/// catálogo para esa ciudad (la app lo pinta como "no está en LocalList todavía"). NUNCA se crea
/// un place automáticamente — la curación es del admin.
/// </summary>
public sealed record MatchedImportPlace(
    ExtractedVideoPlace Candidate,
    Guid? MatchedPlaceId,
    string? MatchedPlaceName,
    string? MatchConfidence);

/// <summary>
/// F2 T3 — matching DETERMINISTA (v1) de los candidatos extraídos de un vídeo contra el catálogo
/// de places PUBLICADOS de la ciudad detectada. Sin Google (coste/ToS), sin fuzzy/trgm de DB:
/// una sola query trae los places de la ciudad (proyección id/name/city) y el matching corre en
/// memoria (los catálogos por ciudad son pequeños, ~100s; el import es Plus-only + rate-limited,
/// así que el barrido es despreciable).
///
/// Estrategia (documentada, reproducible):
///   1. Normalización de nombres: se reutiliza <see cref="CityNameNormalizer.Normalize"/> (lower +
///      sin diacríticos + sin control/format) y además se colapsa todo lo no alfanumérico a un
///      único espacio → forma de COMPARACIÓN (nunca se persiste ni se muestra).
///   2. Tokens "significativos" = tokens de esa forma menos una lista corta de ruido genérico
///      (artículos/conjunciones + "restaurant/bar/cafe/coffee/…"). Si el nombre es SOLO ruido,
///      se cae a todos sus tokens (evita reducirlo a cero).
///   3. Confianza:
///      - <c>high</c>: igualdad normalizada exacta, O uno es un run contiguo de tokens del otro
///        con el lado corto ≥ 5 chars (el guard de longitud evita falsos por palabras minúsculas).
///      - <c>medium</c>: solape de tokens significativos ≥ 60% de los del candidato Y ≥ 2 tokens
///        en común (así un único token genérico —"cafe", "beach"— NUNCA produce match).
///      - sin match → null.
///   4. Empates: mayor confianza → mayor solape → nombre más "ajustado" (menos tokens) → nombre
///      normalizado más corto → menor Id. Orden total ⇒ mismo resultado en cada run.
///
/// Nota v1: el extractor NO aporta coordenadas ni zona fiable (solo <c>Descriptor</c> libre y
/// <c>TimestampSec</c>), así que el matching es SOLO por nombre. Si en el futuro el extractor
/// emite geo, se puede añadir como refuerzo/desempate sin cambiar el contrato.
/// </summary>
public sealed class ImportMatchingService
{
    public const string ConfidenceHigh = "high";
    public const string ConfidenceMedium = "medium";

    /// <summary>Longitud mínima del lado corto para aceptar un "contains" como <c>high</c>.</summary>
    private const int ContainsMinShorterLength = 5;

    /// <summary>Fracción de tokens del candidato que deben solapar para <c>medium</c>.</summary>
    private const double MediumOverlapRatio = 0.6;

    /// <summary>Mínimo absoluto de tokens en común para <c>medium</c> (mata el token genérico único).</summary>
    private const int MediumMinTokens = 2;

    /// <summary>
    /// Ruido genérico que se ignora SOLO para comparar (no distingue un sitio de otro). Deliberadamente
    /// corto y conservador: nada que pueda ser el núcleo distintivo de un nombre real ("house", "grill"
    /// quedan fuera a propósito — p.ej. "Waffle House").
    /// </summary>
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.Ordinal)
    {
        "the", "a", "el", "la", "los", "las", "de", "del", "y", "and",
        "restaurant", "restaurante", "bar", "cafe", "coffee",
    };

    private readonly LocalListDbContext _db;

    public ImportMatchingService(LocalListDbContext db) => _db = db;

    /// <summary>
    /// Enriquece cada candidato con su match en el catálogo de <paramref name="detectedCity"/>.
    /// Los no matcheados se devuelven igual (con campos de match a null). Ciudad detectada ausente
    /// o no presente en el catálogo → todos unmatched, SIN error.
    /// </summary>
    public async Task<IReadOnlyList<MatchedImportPlace>> MatchAsync(
        string? detectedCity, IReadOnlyList<ExtractedVideoPlace> candidates, CancellationToken ct = default)
    {
        if (candidates.Count == 0) return Array.Empty<MatchedImportPlace>();

        var normalizedCity = CityNameNormalizer.Normalize(detectedCity ?? string.Empty);
        if (normalizedCity.Length == 0)
            return candidates.Select(Unmatched).ToList();

        // UNA query: solo places publicados, proyectados al mínimo. City se guarda en crudo (no hay
        // columna normalizada en Place), así que el filtro por ciudad normalizada corre en memoria.
        var published = await _db.Places
            .Where(p => p.Status == "published")
            .Select(p => new { p.Id, p.Name, p.City })
            .ToListAsync(ct);

        var catalog = published
            .Where(p => CityNameNormalizer.Normalize(p.City) == normalizedCity)
            .Select(p =>
            {
                var full = NormalizeName(p.Name);
                var core = CoreTokens(full);
                return new CatalogPlace(p.Id, p.Name, full, core, new HashSet<string>(core, StringComparer.Ordinal));
            })
            .OrderBy(p => p.Id) // orden total → desempate determinista
            .ToList();

        if (catalog.Count == 0)
            return candidates.Select(Unmatched).ToList();

        return candidates.Select(c => MatchOne(c, catalog)).ToList();
    }

    private static MatchedImportPlace MatchOne(ExtractedVideoPlace candidate, List<CatalogPlace> catalog)
    {
        var cFull = NormalizeName(candidate.Name);
        if (cFull.Length == 0) return Unmatched(candidate);

        var cCore = CoreTokens(cFull);
        var cCoreSet = new HashSet<string>(cCore, StringComparer.Ordinal);

        CatalogPlace? best = null;
        var bestTier = 0;
        var bestOverlap = 0;

        // catalog viene en orden de Id ascendente; solo reemplazamos ante mejora ESTRICTA, así un
        // empate total conserva el primero (menor Id) → determinismo.
        foreach (var p in catalog)
        {
            var (tier, overlap) = Evaluate(cFull, cCore, cCoreSet, p);
            if (tier == 0) continue;
            if (best is null || IsBetter(tier, overlap, p, bestTier, bestOverlap, best))
            {
                best = p;
                bestTier = tier;
                bestOverlap = overlap;
            }
        }

        if (best is null) return Unmatched(candidate);
        var confidence = bestTier >= 2 ? ConfidenceHigh : ConfidenceMedium;
        return new MatchedImportPlace(candidate, best.Id, best.Name, confidence);
    }

    /// <summary>Devuelve (tier, solape). tier: 3=high-exacto, 2=high-contains, 1=medium, 0=sin match.</summary>
    private static (int Tier, int Overlap) Evaluate(
        string cFull, string[] cCore, HashSet<string> cCoreSet, CatalogPlace p)
    {
        if (p.FullNorm.Length == 0) return (0, 0);

        // HIGH — igualdad normalizada exacta.
        if (cFull == p.FullNorm) return (3, cCore.Length);

        // HIGH — uno es un run contiguo de tokens del otro (padding con espacios ⇒ límites de token,
        // "crab" no matchea "crabby") y el lado corto es suficientemente largo para no ser casual.
        var shorter = Math.Min(cFull.Length, p.FullNorm.Length);
        if (shorter >= ContainsMinShorterLength)
        {
            var padC = " " + cFull + " ";
            var padP = " " + p.FullNorm + " ";
            if (padP.Contains(padC, StringComparison.Ordinal) || padC.Contains(padP, StringComparison.Ordinal))
                return (2, Math.Max(cCore.Length, p.CoreTokens.Length));
        }

        // MEDIUM — solape de tokens significativos. Nunca con < 2 tokens en común.
        if (cCore.Length >= MediumMinTokens)
        {
            var overlap = cCoreSet.Count(p.CoreSet.Contains);
            var required = Math.Max(MediumMinTokens, (int)Math.Ceiling(MediumOverlapRatio * cCore.Length));
            if (overlap >= required) return (1, overlap);
        }

        return (0, 0);
    }

    private static bool IsBetter(
        int tier, int overlap, CatalogPlace p, int bestTier, int bestOverlap, CatalogPlace best)
    {
        if (tier != bestTier) return tier > bestTier;
        if (overlap != bestOverlap) return overlap > bestOverlap;
        if (p.CoreTokens.Length != best.CoreTokens.Length) return p.CoreTokens.Length < best.CoreTokens.Length;
        if (p.FullNorm.Length != best.FullNorm.Length) return p.FullNorm.Length < best.FullNorm.Length;
        // Empate total: iteramos por Id ascendente y esto es estricto ⇒ se conserva el de menor Id.
        return false;
    }

    private static MatchedImportPlace Unmatched(ExtractedVideoPlace c) => new(c, null, null, null);

    /// <summary>
    /// Forma de comparación: reutiliza el normalizador del repo (lower + sin diacríticos +
    /// sin control/format) y colapsa cada run no alfanumérico a un único espacio.
    /// "Café Versailles" → "cafe versailles"; "Joe's Stone Crab" → "joe s stone crab".
    /// </summary>
    private static string NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var baseNorm = CityNameNormalizer.Normalize(raw);
        var sb = new StringBuilder(baseNorm.Length);
        var pendingSpace = false;
        foreach (var ch in baseNorm)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(ch);
            }
            else
            {
                pendingSpace = true;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Tokens significativos (sin ruido genérico). Si el nombre es ÍNTEGRAMENTE ruido
    /// (p.ej. "The Bar"), cae a todos sus tokens para no quedarse sin señal.
    /// </summary>
    private static string[] CoreTokens(string fullNorm)
    {
        if (fullNorm.Length == 0) return Array.Empty<string>();
        var tokens = fullNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var core = tokens.Where(t => !NoiseTokens.Contains(t)).ToArray();
        return core.Length > 0 ? core : tokens;
    }

    private sealed record CatalogPlace(
        Guid Id, string Name, string FullNorm, string[] CoreTokens, HashSet<string> CoreSet);
}
