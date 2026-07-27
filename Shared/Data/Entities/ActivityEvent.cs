using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

/// <summary>
/// Evento de actividad append-only — primitiva del feed (S1+). actor_id FK users CASCADE.
/// object_id es polimorfico (SIN FK dura). UNIQUE (actor_id, verb, object_id) da idempotencia.
/// </summary>
[Table("activity_events")]
public class ActivityEvent
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("actor_id")]
    public Guid ActorId { get; set; }

    /// <summary>'plan_published' | 'user_followed' | 'favorite_added' | 'plan_imported'.</summary>
    [Column("verb")]
    [StringLength(30)]
    [Required]
    public string Verb { get; set; } = string.Empty;

    [Column("object_type")]
    [StringLength(20)]
    [Required]
    public string ObjectType { get; set; } = string.Empty;

    /// <summary>Referencia polimorfica al objeto del evento. SIN FK dura (puede apuntar a plan/user/...).</summary>
    [Column("object_id")]
    public Guid ObjectId { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
