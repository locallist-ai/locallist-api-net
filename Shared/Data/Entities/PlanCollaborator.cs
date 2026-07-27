using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

/// <summary>
/// Colaborador de un plan (co-edicion, S1+). El OWNER no es fila aqui (sigue en plans.created_by).
/// PK compuesta (plan_id, user_id). plan_id/user_id FK CASCADE; invited_by FK users SET NULL.
/// </summary>
[Table("plan_collaborators")]
public class PlanCollaborator
{
    [Column("plan_id")]
    public Guid PlanId { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    /// <summary>'editor' | 'viewer'.</summary>
    [Column("role")]
    [StringLength(10)]
    [Required]
    public string Role { get; set; } = "viewer";

    [Column("invited_by")]
    public Guid? InvitedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
