namespace LocalList.API.NET.Shared.Coverage;

/// <summary>
/// Contrato cross-slice del gate de cobertura. Vive en Shared porque lo consumen
/// varias features (Cities expone el selector, Chat bloquea ciudades no cubiertas).
/// La implementación vive en la feature dueña (<c>Features/Cities/</c>) porque
/// normaliza con <c>CityNameNormalizer</c>.
/// </summary>
public interface ICityCoverageService
{
    /// <summary>Nombres de ciudad LIVE tal y como se configuraron (casing de display).</summary>
    IReadOnlyList<string> LiveCities { get; }

    /// <summary>
    /// True si la ciudad está en la allowlist. Compara de forma insensible a
    /// acentos/mayúsculas vía la normalización canónica. Null/vacío → false.
    /// </summary>
    bool IsLive(string? cityName);
}
