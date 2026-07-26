namespace LocalList.API.NET.Shared.Constants;

public static class PriceRanges
{
    public const string Free = "FREE";
    public const string Cheap = "$";
    public const string Mid = "$$";
    public const string Expensive = "$$$";
    public const string Premium = "$$$$";

    public static readonly string[] All = [Free, Cheap, Mid, Expensive, Premium];

    public static bool IsValid(string? v) => v == null || All.Contains(v);

    /// <summary>
    /// Normaliza un valor crudo de PriceRange a su forma canónica y valida.
    /// Trim + uppercase invariante para tolerar entradas escritas a mano
    /// ("free"/"Free"/" FREE " → "FREE", " $ " → "$"). Vacío/whitespace → null (válido).
    /// Devuelve <c>true</c> si el resultado es canónico (o vacío→null); <c>false</c> si tras
    /// normalizar sigue sin ser un rango reconocido ("€€", "cheap", ...). En ese caso
    /// <paramref name="normalized"/> queda en null.
    /// </summary>
    public static bool TryNormalize(string? raw, out string? normalized)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            normalized = null;
            return true;
        }

        var candidate = raw.Trim().ToUpperInvariant();
        if (All.Contains(candidate))
        {
            normalized = candidate;
            return true;
        }

        normalized = null;
        return false;
    }
}
