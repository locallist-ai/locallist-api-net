using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Shared.AI.Services;

public interface IPlaceTranslatorService
{
    Task<PlaceTranslationDraft?> TranslatePlaceAsync(Place place, string targetLang = "es", CancellationToken ct = default);
    Task<PlanTranslationDraft?> TranslatePlanAsync(Plan plan, string targetLang = "es", CancellationToken ct = default);
}
