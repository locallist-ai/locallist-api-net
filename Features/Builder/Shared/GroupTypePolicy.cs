namespace LocalList.API.NET.Features.Builder.Shared;

/// <summary>
/// Single source of truth para las reglas basadas en <c>groupType</c>.
///
/// Antes de esta clase, la lógica "es family/family-kids" estaba duplicada en
/// <c>AiProviderService.ExtractWithKeywords</c>, <c>PlaceRankingService.ScoreSuitableForMatch</c>
/// y <c>BuilderController.IsGoodTimeMatch</c>. Si añadíamos un nuevo groupType
/// (p.ej. "couple-with-pet") había que tocar 3 sitios en sync.
/// </summary>
public static class GroupTypePolicy
{
    /// <summary>
    /// True si el groupType implica contexto familiar (family o family-kids).
    /// Para estos grupos aplicamos reglas de exclusión: nightlife fuera, adults-only fuera.
    /// </summary>
    public static bool IsFamilyContext(string? groupType) =>
        string.Equals(groupType, "family", System.StringComparison.OrdinalIgnoreCase)
        || string.Equals(groupType, "family-kids", System.StringComparison.OrdinalIgnoreCase);
}
