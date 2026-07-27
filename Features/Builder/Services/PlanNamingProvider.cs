using LocalList.API.NET.Shared.AI.Services;
using LocalList.API.NET.Shared.Dtos;

namespace LocalList.API.NET.Features.Builder.Services;

/// <summary>
/// Implementación de <see cref="IPlanNamingService"/> para el contrato cross-slice. Delega en el
/// helper estático <see cref="PlanNamingService"/> (dueño de la lógica de naming, usado además
/// dentro del propio slice Builder de forma estática). Este adaptador solo existe para que Import
/// consuma el naming por interfaz sin acoplarse al slice Builder (BLOCKER 2 de boundaries VSA).
/// </summary>
public sealed class PlanNamingProvider : IPlanNamingService
{
    public string BuildPlanName(ExtractedPreferences prefs, string city, string rawMessage, string lang = "en")
        => PlanNamingService.BuildPlanName(prefs, city, rawMessage, lang);
}
