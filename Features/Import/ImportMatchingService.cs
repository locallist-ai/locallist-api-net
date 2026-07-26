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
/// una sola query trae los places published y el matching corre en memoria (los catálogos por
/// ciudad son pequeños, ~100s; el import es Plus-only + rate-limited, así que el barrido es
/// despreciable).
///
/// Estrategia (documentada, reproducible):
///   1. Normalización de nombres: se reutiliza <see cref="CityNameNormalizer.Normalize"/> (lower +
///      sin diacríticos + sin control/format) y además se colapsa todo lo no alfanumérico a un
///      único espacio → forma de COMPARACIÓN (nunca se persiste ni se muestra).
///   2. Tokens "core" = tokens de esa forma menos una lista corta de ruido genérico
///      (artículos/conjunciones + "restaurant/bar/cafe/coffee/…"). Si el nombre es SOLO ruido,
///      se cae a todos sus tokens (evita reducirlo a cero).
///   3. Confianza:
///      - <c>high</c>: igualdad normalizada exacta, O los tokens CORE de un lado forman un run
///        CONTIGUO dentro de los del otro con el lado corto aportando ≥ 2 tokens core. Un
///        contains de un único token está PROHIBIDO da igual su longitud en chars ("havana" ⊂
///        "little havana cafe", "grill" ⊂ "the rusty grill" NO matchean), y el ruido genérico
///        se descarta ANTES de comparar ("cafe cubano" no hace contains contra "cubano").
///      - <c>medium</c>: solape de tokens core ≥ 60% de los del candidato Y ≥ 2 tokens en común
///        (así un único token —genérico o no— NUNCA produce match). Medium es bag-of-words
///        (ignora el orden): "casa marina" ↔ "marina casa club" puede matchear — ACEPTADO v1;
///        medium es una SUGERENCIA para la app, no un enlace fuerte.
///      - sin match → null.
///   4. Ranking/empates: mayor tier → (solo en medium) mayor solape → menos tokens core (más
///      "ajustado") → nombre normalizado más corto. En contains el solape NO participa (inflaba
///      el ranking hacia el nombre MÁS LARGO). Si tras todo eso ≥2 places DISTINTOS empatan en la
///      misma tupla (cadenas/franquicias: dos "Starbucks" en la ciudad), NO se elige uno
///      arbitrario por Id: el candidato queda SIN match — suprimir &gt; enlazar la sucursal
///      equivocada. El resultado sigue siendo determinista (misma entrada ⇒ misma salida).
///
/// Nota v1: el extractor NO aporta coordenadas ni zona fiable (solo <c>Descriptor</c> libre y
/// <c>TimestampSec</c>), así que el matching es SOLO por nombre. Si en el futuro el extractor
/// emite geo, se puede añadir como refuerzo/desempate sin cambiar el contrato.
/// </summary>
public sealed class ImportMatchingService
{
    public const string ConfidenceHigh = "high";
    public const string ConfidenceMedium = "medium";

    /// <summary>
    /// Mínimo de tokens core que debe aportar el lado CORTO para aceptar un "contains" como
    /// <c>high</c>. Un contains de 1 token está prohibido da igual su longitud en chars: es la
    /// firma exacta del falso positivo ("havana", "grill", nombres de barrio/genéricos).
    /// </summary>
    private const int ContainsMinCoreTokens = 2;

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
        // columna normalizada en Place), así que el filtro por ciudad normalizada corre EN MEMORIA
        // sobre TODO el catálogo published — aceptable hoy (Miami-only, ~100s de filas).
        // TODO(multi-ciudad): añadir columna `normalized_city` indexada y empujar el predicado a SQL
        // para no barrer el catálogo completo por import.
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
            // Orden estable por Id: el resultado NO depende de él (un empate de tupla → null por
            // ambigüedad, jamás "el primero"), pero fija el orden de iteración para reproducibilidad.
            .OrderBy(p => p.Id)
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
        var ambiguous = false;

        foreach (var p in catalog)
        {
            var (tier, overlap) = Evaluate(cFull, cCore, cCoreSet, p);
            if (tier == 0) continue;
            if (best is null)
            {
                (best, bestTier, bestOverlap) = (p, tier, overlap);
                continue;
            }
            var cmp = CompareRank(tier, overlap, p, bestTier, bestOverlap, best);
            if (cmp > 0)
            {
                (best, bestTier, bestOverlap) = (p, tier, overlap);
                ambiguous = false;
            }
            else if (cmp == 0)
            {
                // Empate GENUINO entre ≥2 places distintos en la misma tupla de ranking
                // (cadena/franquicia: dos "Starbucks"). Elegir por Id sería una sucursal
                // arbitraria reportada como match → se suprime el match entero.
                ambiguous = true;
            }
        }

        if (best is null || ambiguous) return Unmatched(candidate);
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

        // HIGH — contains SOBRE TOKENS CORE (el ruido genérico ya está fuera: "cafe cubano" no
        // hace contains contra "cubano"): los tokens core del lado corto deben aparecer como run
        // CONTIGUO dentro de los del otro, y ese lado corto debe aportar ≥ 2 tokens core. Un
        // contains de 1 token está prohibido da igual su longitud en chars ("havana", "grill").
        if (Math.Min(cCore.Length, p.CoreTokens.Length) >= ContainsMinCoreTokens &&
            IsContiguousRun(cCore, p.CoreTokens))
        {
            // Overlap = 0 a propósito: en contains el solape NO participa en el ranking (usar
            // max(tokens) prefería el nombre MÁS LARGO, invirtiendo el desempate "más ajustado").
            return (2, 0);
        }

        // MEDIUM — solape de tokens core (bag-of-words). Nunca con < 2 tokens en común.
        if (cCore.Length >= MediumMinTokens)
        {
            var overlap = cCoreSet.Count(p.CoreSet.Contains);
            var required = Math.Max(MediumMinTokens, (int)Math.Ceiling(MediumOverlapRatio * cCore.Length));
            if (overlap >= required) return (1, overlap);
        }

        return (0, 0);
    }

    /// <summary>
    /// ¿La secuencia de tokens más corta aparece como run contiguo dentro de la más larga?
    /// Comparación por token completo (nunca substring: "crab" no matchea "crabby").
    /// </summary>
    private static bool IsContiguousRun(string[] a, string[] b)
    {
        var (needle, hay) = a.Length <= b.Length ? (a, b) : (b, a);
        for (var start = 0; start + needle.Length <= hay.Length; start++)
        {
            var found = true;
            for (var i = 0; i < needle.Length; i++)
            {
                if (!string.Equals(needle[i], hay[start + i], StringComparison.Ordinal))
                {
                    found = false;
                    break;
                }
            }
            if (found) return true;
        }
        return false;
    }

    /// <summary>
    /// Ranking total menos el Id: &gt;0 si <paramref name="p"/> es estrictamente mejor, &lt;0 si
    /// peor, 0 si la tupla (tier, overlap, coreTokens, fullNormLen) es IDÉNTICA — ese 0 es el que
    /// el caller convierte en ambigüedad (null), nunca en una elección arbitraria por Id.
    /// </summary>
    private static int CompareRank(
        int tier, int overlap, CatalogPlace p, int bestTier, int bestOverlap, CatalogPlace best)
    {
        if (tier != bestTier) return tier.CompareTo(bestTier);
        if (overlap != bestOverlap) return overlap.CompareTo(bestOverlap);
        // Más "ajustado" gana: menos tokens core, y a igualdad, nombre normalizado más corto.
        if (p.CoreTokens.Length != best.CoreTokens.Length)
            return best.CoreTokens.Length.CompareTo(p.CoreTokens.Length);
        if (p.FullNorm.Length != best.FullNorm.Length)
            return best.FullNorm.Length.CompareTo(p.FullNorm.Length);
        return 0;
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
    /// Tokens core (sin ruido genérico). Si el nombre es ÍNTEGRAMENTE ruido
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
