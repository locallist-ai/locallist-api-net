using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Routing;

/// <summary>
/// Resolves a single route segment between two places, using the DB cache when available.
/// Implemented by <see cref="RouteResolver"/>; extracted as an interface for testability.
/// </summary>
public interface ISegmentResolver
{
    /// <summary>
    /// Returns the route segment from <paramref name="from"/> to <paramref name="to"/>,
    /// reading from the DB cache on hit and writing on miss. Returns null when either
    /// place lacks coordinates or when the upstream routing service is unavailable.
    /// </summary>
    Task<RouteSegment?> ResolveSegmentAsync(
        Place from, Place to, RoutingMode mode, CancellationToken ct);
}
