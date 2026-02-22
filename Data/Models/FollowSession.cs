using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Data.Models;

[Table("follow_sessions")]
public class FollowSession
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("plan_id")]
    public Guid PlanId { get; set; }

    [Column("status")]
    [StringLength(20)]
    public string Status { get; set; } = "active";

    [Column("current_day_index")]
    public int CurrentDayIndex { get; set; } = 1;

    [Column("current_stop_index")]
    public int CurrentStopIndex { get; set; } = 0;

    [Column("started_at")]
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }

    [Column("last_active_at")]
    public DateTimeOffset LastActiveAt { get; set; } = DateTimeOffset.UtcNow;

    public User? User { get; set; }
    public Plan? Plan { get; set; }
}
