namespace LocalList.API.NET.Shared.Dtos;

public record PlanRouteSegmentDto(
    int DayNumber,
    int FromOrderIndex,
    int ToOrderIndex,
    string EncodedPolyline,
    int DistanceMeters,
    int DurationSeconds
);
