using LocalList.API.NET.Features.Builder.Shared;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Builder.Services;

// Legacy helpers (kept for existing tests and callers): preference refinements (exclusions,
// dietary), time-block matching, and resolution of scheduled stops to place DTOs. Logic is
// identical to the original single-file version; only its location changed.
public partial class SchedulingService
{
    internal List<Place> ApplyRefinements(List<Place> places, ExtractedPreferences prefs, ScheduleResult result)
    {
        var current = places;

        // Exclusions
        var exclusions = prefs.Exclusions;
        if (exclusions != null && exclusions.Count > 0)
        {
            var filtered = current
                .Where(p => !exclusions.Any(e =>
                    string.Equals(p.Category, e, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (filtered.Count == 0 && current.Count > 0)
            {
                _logger.LogWarning(
                    "Builder: exclusion_fallback exclusions=[{Ex}] totalPlaces={N} — using all",
                    string.Join(",", exclusions), current.Count);
                result.Warnings.Add("exclusion_fallback");
            }
            else
            {
                current = filtered;
                result.AppliedRefinements.Add($"excluded:{string.Join(",", exclusions)}");
            }
        }

        // Dietary — soft filter: places with SuitableFor data must match;
        // places without SuitableFor are always included (no info = safe to include).
        var dietary = prefs.Dietary;
        if (dietary != null && dietary.Count > 0 &&
            !dietary.Contains("none", StringComparer.OrdinalIgnoreCase))
        {
            var withDietary = current
                .Where(p =>
                    p.SuitableFor == null || p.SuitableFor.Count == 0 ||
                    dietary.Any(d => p.SuitableFor.Any(sf =>
                        sf.Contains(d, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            if (withDietary.Count == 0 && current.Count > 0)
            {
                _logger.LogWarning(
                    "Builder: dietary_no_matches dietary=[{D}] — using all",
                    string.Join(",", dietary));
                result.Warnings.Add("dietary_no_matches");
            }
            else if (withDietary.Count < current.Count)
            {
                current = withDietary;
                result.AppliedRefinements.Add($"dietary:{string.Join(",", dietary)}");
            }
        }

        return current;
    }

    internal bool IsGoodTimeMatch(Place place, string timeBlock, ExtractedPreferences prefs, bool strict)
    {
        if (strict)
        {
            if (GroupTypePolicy.IsFamilyContext(prefs.GroupType) &&
                string.Equals(place.Category, "nightlife", StringComparison.OrdinalIgnoreCase))
                return false;

            if (TimeBlockCategories.TryGetValue(timeBlock, out var allowedCategories))
            {
                if (!allowedCategories.Contains(place.Category))
                    return false;
            }
        }

        if (place.BestTimes is not { Count: > 0 }) return true;
        if (place.BestTimes.Any(bt => string.IsNullOrEmpty(bt) || bt.ToLower() == "any")) return true;
        if (BestTimeMatches.TryGetValue(timeBlock, out var matchingTimes))
            return place.BestTimes.Any(bt => matchingTimes.Any(t => bt.ToLower().Contains(t)));

        return true;
    }

    public IEnumerable<ScheduledStopResult> ResolveStopPlaces(List<ScheduledStopDto> stops, List<Place> allPlaces)
    {
        var placeMap = allPlaces.ToDictionary(p => p.Id);
        var publicBaseUrl = _config?["Api:PublicBaseUrl"];

        return stops.Select(stop =>
        {
            placeMap.TryGetValue(stop.PlaceId, out var place);
            ResolvedPlaceDto? placeDto = null;
            if (place != null)
            {
                // Misma síntesis que PlaceDto: nunca reemitir aquí una URL
                // places.googleapis.com (con key). El proxy de fotos (T1) es el único
                // sitio autorizado a resolver la key server-side.
                var (photos, photoSource) = PlacePhotoUrls.Resolve(
                    place.Id, place.GooglePlaceId, place.Photos, publicBaseUrl);

                placeDto = new ResolvedPlaceDto(
                    Id: place.Id,
                    Name: place.Name,
                    Category: place.Category,
                    Neighborhood: place.Neighborhood,
                    WhyThisPlace: place.WhyThisPlace,
                    PriceRange: place.PriceRange,
                    Photos: photos,
                    Latitude: place.Latitude,
                    Longitude: place.Longitude,
                    PhotoSource: photoSource);
            }

            return new ScheduledStopResult(
                Id: Guid.NewGuid(),
                PlaceId: stop.PlaceId,
                DayNumber: stop.DayNumber,
                OrderIndex: stop.OrderIndex,
                TimeBlock: stop.TimeBlock,
                SuggestedArrival: stop.SuggestedArrival,
                SuggestedDurationMin: stop.SuggestedDurationMin,
                TravelFromPrevious: stop.TravelFromPrevious,
                Place: placeDto);
        });
    }
}
