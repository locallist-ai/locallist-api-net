namespace LocalList.API.NET.Shared.Routing;

public interface IRoutingService
{
    /// <summary>
    /// Returns the walking/driving route between two points.
    /// Returns null on upstream failure — callers should fall back to straight-line rendering.
    /// </summary>
    Task<RouteSegment?> GetRouteAsync(GeoPoint from, GeoPoint to, RoutingMode mode, CancellationToken ct);
}
