namespace LocalList.API.NET.Features.Routing;

public record GeoPoint(decimal Lat, decimal Lng);

public record RouteSegment(string EncodedPolyline, int DistanceMeters, int DurationSeconds);

public enum RoutingMode { Walking, Driving }
