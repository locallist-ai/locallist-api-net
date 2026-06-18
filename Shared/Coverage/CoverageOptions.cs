namespace LocalList.API.NET.Shared.Coverage;

/// <summary>
/// Configuración de la cobertura de ciudades "en vivo". El gate se define por
/// una allowlist explícita (<c>Coverage:LiveCities</c>), nunca por "la ciudad
/// tiene places" — hay ciudades de TEST en la admin con places que NO deben
/// exponerse a la app.
///
/// Lectura por entorno: <c>Coverage__LiveCities__0=Miami</c> (índices) o, como
/// atajo, <c>Coverage__LiveCities=Miami,Sevilla</c> (lista separada por comas).
/// Si no se configura nada, el default es <c>["Miami"]</c>.
/// </summary>
public static class CoverageOptions
{
    public const string SectionName = "Coverage";
    public const string LiveCitiesKey = "Coverage:LiveCities";

    /// <summary>Allowlist por defecto cuando no hay configuración explícita.</summary>
    public static readonly string[] DefaultLiveCities = { "Miami" };
}
