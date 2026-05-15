using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

[Table("plan_metrics")]
public class PlanMetric
{
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("plan_id")]
    public Guid PlanId { get; set; }

    [Column("chat_session_id")]
    public Guid? ChatSessionId { get; set; }

    [Column("generate_turn_id")]
    public Guid? GenerateTurnId { get; set; }

    [Column("generation_source")]
    public string GenerationSource { get; set; } = "chat";

    [Column("signals_filled")]
    public short SignalsFilled { get; set; }

    [Column("num_days")]
    public int NumDays { get; set; }

    [Column("num_stops")]
    public int NumStops { get; set; }

    [Column("num_categories")]
    public int NumCategories { get; set; }

    [Column("group_type")]
    public string? GroupType { get; set; }

    [Column("budget")]
    public string? Budget { get; set; }

    [Column("vibes_json", TypeName = "jsonb")]
    public string? VibesJson { get; set; }

    [Column("prompt_version")]
    public string? PromptVersion { get; set; }

    [Column("latency_ms")]
    public int LatencyMs { get; set; }

    [Column("cost_usd")]
    public decimal? CostUsd { get; set; }

    [Column("was_opened")]
    public bool WasOpened { get; set; }

    [Column("opened_at")]
    public DateTimeOffset? OpenedAt { get; set; }

    [Column("was_followed")]
    public bool WasFollowed { get; set; }

    [Column("followed_at")]
    public DateTimeOffset? FollowedAt { get; set; }

    [Column("edited_count")]
    public int EditedCount { get; set; }

    [Column("regenerated")]
    public bool Regenerated { get; set; }

    // Nav
    public Plan? Plan { get; set; }
}
