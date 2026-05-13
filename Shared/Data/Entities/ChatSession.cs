using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace LocalList.API.NET.Shared.Data.Entities;

[Table("chat_sessions")]
public class ChatSession
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid? UserId { get; set; }

    [Column("anonymous_ip_hash")]
    [StringLength(64)]
    public string? AnonymousIpHash { get; set; }

    [Column("started_at")]
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("last_turn_at")]
    public DateTimeOffset LastTurnAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("turn_count")]
    public int TurnCount { get; set; } = 0;

    [Column("status")]
    [StringLength(20)]
    public string Status { get; set; } = "active"; // active | ready | generated | abandoned

    // JSON columns stored as text — avoids Npgsql jsonb mapping complexity with EF Core 9
    [Column("slots", TypeName = "text")]
    public string SlotsJson { get; set; } = "{}";

    [Column("history", TypeName = "text")]
    public string HistoryJson { get; set; } = "[]";

    [Column("generated_plan_id")]
    public Guid? GeneratedPlanId { get; set; }

    // Security columns (PR 1.5)
    [Column("suspicion", TypeName = "text")]
    public string SuspicionJson { get; set; } = "{}";

    // Chip IDs offered in the last AI response — validated on next turn to prevent chip forgery
    [Column("last_offered_chips", TypeName = "text[]")]
    public string[] LastOfferedChips { get; set; } = Array.Empty<string>();

    public User? User { get; set; }
}
