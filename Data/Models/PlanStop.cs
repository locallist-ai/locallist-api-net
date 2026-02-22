using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace LocalList.API.NET.Data.Models;

[Table("plan_stops")]
public class PlanStop
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("plan_id")]
    public Guid PlanId { get; set; }

    [Column("place_id")]
    public Guid PlaceId { get; set; }

    [Column("day_number")]
    public int DayNumber { get; set; }

    [Column("order_index")]
    public int OrderIndex { get; set; }

    [Column("time_block")]
    [StringLength(20)]
    public string? TimeBlock { get; set; }

    [Column("suggested_arrival")]
    public TimeSpan? SuggestedArrival { get; set; }

    [Column("suggested_duration_min")]
    public int? SuggestedDurationMin { get; set; }

    [Column("travel_from_previous", TypeName = "jsonb")]
    public JsonDocument? TravelFromPrevious { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Plan? Plan { get; set; }
    public Place? Place { get; set; }
}
