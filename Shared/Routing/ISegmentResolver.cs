using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;

namespace LocalList.API.NET.Shared.Routing;

/// <summary>
/// Resolves route segments between places, using the DB cache when available.
/// Implemented by <c>RouteResolver</c>; extracted as an interface for testability
/// and to keep consumers (PlansController, SchedulingService) off the concrete type.
/// </summary>
public interface ISegmentResolver
{
    /// <summary>
    /// Batch-resolves the consecutive-stop segments for a plan's stops (PlansController read path),
    /// reading from the DB cache on hit and writing on miss. Days with unrealistic stop counts or
    /// stops missing coordinates are skipped.
    /// </summary>
    Task<List<PlanRouteSegmentDto>> ResolveAsync(
        ICollection<PlanStop> stops, RoutingMode mode, CancellationToken ct);

    /// <summary>
    /// Returns the route segment from <paramref name="from"/> to <paramref name="to"/>,
    /// reading from the DB cache on hit and writing on miss. Returns null when either
    /// place lacks coordinates or when the upstream routing service is unavailable.
    /// </summary>
    Task<RouteSegment?> ResolveSegmentAsync(
        Place from, Place to, RoutingMode mode, CancellationToken ct);
}
