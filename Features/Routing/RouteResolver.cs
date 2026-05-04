using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using LocalList.API.NET.Features.Plans;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Routing;

public class RouteResolver
{
    private readonly LocalListDbContext _db;
    private readonly IRoutingService _routing;
    private readonly ILogger<RouteResolver> _logger;

    public RouteResolver(LocalListDbContext db, IRoutingService routing, ILogger<RouteResolver> logger)
    {
        _db = db;
        _routing = routing;
        _logger = logger;
    }

    public async Task<List<PlanRouteSegmentDto>> ResolveAsync(
        ICollection<PlanStop> stops,
        RoutingMode mode,
        CancellationToken ct)
    {
        var pairs = ExtractPairs(stops);
        if (pairs.Count == 0) return [];

        var modeStr = mode.ToString().ToLowerInvariant();

        // Batch cache lookup: filter by all place IDs involved, then match pairs in-memory.
        var placeIds = pairs.SelectMany(p => new[] { p.From.PlaceId, p.To.PlaceId }).Distinct().ToList();
        var cachedRows = await _db.RouteSegmentCaches
            .Where(r => placeIds.Contains(r.FromPlaceId) && placeIds.Contains(r.ToPlaceId) && r.Mode == modeStr)
            .ToListAsync(ct);

        var cacheHits = cachedRows.ToDictionary(r => (r.FromPlaceId, r.ToPlaceId));
        var misses = pairs.Where(p => !cacheHits.ContainsKey((p.From.PlaceId, p.To.PlaceId))).ToList();

        if (misses.Count > 0)
        {
            _logger.LogInformation("RouteResolver: {MissCount} cache misses, calling Mapbox", misses.Count);
            var newRows = await FetchAndPersistAsync(misses, mode, modeStr, ct);
            foreach (var row in newRows)
                cacheHits[(row.FromPlaceId, row.ToPlaceId)] = row;
        }

        return pairs
            .Select(p =>
            {
                if (!cacheHits.TryGetValue((p.From.PlaceId, p.To.PlaceId), out var cached)) return null;
                return new PlanRouteSegmentDto(
                    p.From.DayNumber,
                    p.From.OrderIndex,
                    p.To.OrderIndex,
                    cached.EncodedPolyline,
                    cached.DistanceMeters,
                    cached.DurationSeconds);
            })
            .OfType<PlanRouteSegmentDto>()
            .ToList();
    }

    private async Task<List<RouteSegmentCache>> FetchAndPersistAsync(
        List<(PlanStop From, PlanStop To)> misses,
        RoutingMode mode,
        string modeStr,
        CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(4);
        var tasks = misses.Select(async pair =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var from = new GeoPoint(pair.From.Place!.Latitude!.Value, pair.From.Place.Longitude!.Value);
                var to = new GeoPoint(pair.To.Place!.Latitude!.Value, pair.To.Place.Longitude!.Value);
                var segment = await _routing.GetRouteAsync(from, to, mode, ct);
                return segment is null ? null : new RouteSegmentCache
                {
                    Id = Guid.NewGuid(),
                    FromPlaceId = pair.From.PlaceId,
                    ToPlaceId = pair.To.PlaceId,
                    Mode = modeStr,
                    EncodedPolyline = segment.EncodedPolyline,
                    DistanceMeters = segment.DistanceMeters,
                    DurationSeconds = segment.DurationSeconds,
                    ComputedAt = DateTimeOffset.UtcNow,
                };
            }
            finally { semaphore.Release(); }
        });

        var results = (await Task.WhenAll(tasks)).OfType<RouteSegmentCache>().ToList();
        if (results.Count == 0) return results;

        // Bulk INSERT … ON CONFLICT DO NOTHING to handle concurrent requests for the same plan.
        var sql = new StringBuilder(
            "INSERT INTO route_segment_cache " +
            "(id, from_place_id, to_place_id, mode, encoded_polyline, distance_meters, duration_seconds, computed_at) VALUES ");

        var parameters = new List<NpgsqlParameter>();
        for (int i = 0; i < results.Count; i++)
        {
            if (i > 0) sql.Append(',');
            var b = i * 7;
            sql.Append($"(@p{b},@p{b+1},@p{b+2},@p{b+3},@p{b+4},@p{b+5},@p{b+6},NOW())");
            var row = results[i];
            parameters.AddRange([
                new NpgsqlParameter($"@p{b}", row.Id),
                new NpgsqlParameter($"@p{b+1}", row.FromPlaceId),
                new NpgsqlParameter($"@p{b+2}", row.ToPlaceId),
                new NpgsqlParameter($"@p{b+3}", row.Mode),
                new NpgsqlParameter($"@p{b+4}", row.EncodedPolyline),
                new NpgsqlParameter($"@p{b+5}", row.DistanceMeters),
                new NpgsqlParameter($"@p{b+6}", row.DurationSeconds),
            ]);
        }
        sql.Append(" ON CONFLICT (from_place_id, to_place_id, mode) DO NOTHING");

        await _db.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.Cast<object>().ToArray(), ct);
        return results;
    }

    private static List<(PlanStop From, PlanStop To)> ExtractPairs(ICollection<PlanStop> stops)
    {
        var pairs = new List<(PlanStop, PlanStop)>();
        foreach (var dayGroup in stops.GroupBy(s => s.DayNumber))
        {
            var ordered = dayGroup.OrderBy(s => s.OrderIndex).ToList();
            // Defensive: skip days with unrealistic stop counts
            if (ordered.Count > 20) continue;
            for (int i = 0; i < ordered.Count - 1; i++)
            {
                var from = ordered[i];
                var to = ordered[i + 1];
                if (HasValidCoords(from) && HasValidCoords(to))
                    pairs.Add((from, to));
            }
        }
        return pairs;
    }

    private static bool HasValidCoords(PlanStop s) =>
        s.Place?.Latitude is not null && s.Place?.Longitude is not null;
}
