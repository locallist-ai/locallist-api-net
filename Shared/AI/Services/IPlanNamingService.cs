using LocalList.API.NET.Shared.Dtos;

namespace LocalList.API.NET.Shared.AI.Services;

/// <summary>
/// Contrato cross-slice del naming de planes (nombre bilingüe de fallback). Vive en Shared porque
/// lo consumen Builder (generación) e Import (F2 T4, fallback cuando el usuario no da nombre). La
/// implementación (<c>PlanNamingProvider</c>) vive en <c>Features/Builder/Services/</c> y delega en
/// el helper <c>PlanNamingService</c>; Import no depende del tipo concreto, solo de esta firma.
/// </summary>
public interface IPlanNamingService
{
    /// <summary>
    /// Nombre del plan: si <c>prefs.PlanName</c> es usable lo devuelve; si no, un fallback bilingüe
    /// (EN/ES-España) por <paramref name="lang"/> con ciudad/días/vibe.
    /// </summary>
    string BuildPlanName(ExtractedPreferences prefs, string city, string rawMessage, string lang = "en");
}
