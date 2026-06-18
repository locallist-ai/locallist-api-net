using LocalList.API.NET.Shared.Coverage;

namespace LocalList.API.NET.Features.Cities;

/// <summary>
/// Implementación del gate de cobertura. Resuelve la allowlist de
/// <c>Coverage:LiveCities</c> una sola vez en construcción (singleton — la
/// config es fija en boot) y la normaliza con <see cref="CityNameNormalizer"/>
/// para comparar de forma insensible a acentos/mayúsculas.
///
/// CAVEAT: la cobertura NO se deriva de "la ciudad tiene places". Hay ciudades
/// de TEST en la admin con places que no deben exponerse; solo cuenta la
/// allowlist explícita.
/// </summary>
public sealed class CityCoverageService : ICityCoverageService
{
    private readonly HashSet<string> _liveNormalized;

    public CityCoverageService(IConfiguration config)
    {
        var configured = ReadConfiguredCities(config);
        var names = configured.Count > 0 ? configured : CoverageOptions.DefaultLiveCities.ToList();

        LiveCities = names.AsReadOnly();
        _liveNormalized = names
            .Select(CityNameNormalizer.Normalize)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<string> LiveCities { get; }

    public bool IsLive(string? cityName)
    {
        if (string.IsNullOrWhiteSpace(cityName)) return false;
        return _liveNormalized.Contains(CityNameNormalizer.Normalize(cityName));
    }

    /// <summary>
    /// Lee la allowlist desde config. Soporta el binding estándar de arrays
    /// (<c>Coverage:LiveCities:0</c>, también vía <c>Coverage__LiveCities__0</c>)
    /// y, como atajo de entorno, un escalar separado por comas
    /// (<c>Coverage__LiveCities=Miami,Sevilla</c>).
    /// </summary>
    private static List<string> ReadConfiguredCities(IConfiguration config)
    {
        var section = config.GetSection(CoverageOptions.LiveCitiesKey);
        var children = section.GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToList();
        if (children.Count > 0) return children;

        var scalar = config[CoverageOptions.LiveCitiesKey];
        if (!string.IsNullOrWhiteSpace(scalar))
            return scalar
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

        return new List<string>();
    }
}
