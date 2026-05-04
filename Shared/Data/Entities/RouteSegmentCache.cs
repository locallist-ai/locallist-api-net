using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

[Table("route_segment_cache")]
public class RouteSegmentCache
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("from_place_id")]
    public Guid FromPlaceId { get; set; }

    [Column("to_place_id")]
    public Guid ToPlaceId { get; set; }

    [Column("mode")]
    [StringLength(20)]
    public string Mode { get; set; } = string.Empty;

    [Column("encoded_polyline")]
    public string EncodedPolyline { get; set; } = string.Empty;

    [Column("distance_meters")]
    public int DistanceMeters { get; set; }

    [Column("duration_seconds")]
    public int DurationSeconds { get; set; }

    [Column("computed_at")]
    public DateTimeOffset ComputedAt { get; set; } = DateTimeOffset.UtcNow;

    public Place? FromPlace { get; set; }
    public Place? ToPlace { get; set; }
}
