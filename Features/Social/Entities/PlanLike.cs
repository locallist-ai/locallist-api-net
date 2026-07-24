using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Features.Social.Entities;

/// <summary>
/// Like de un usuario a un plan (S1+). PK compuesta (plan_id, user_id). Ambos FK CASCADE.
/// El contador denormalizado vive en <c>plans.likes_count</c> y se mantiene en la misma transaccion.
/// </summary>
[Table("plan_likes")]
public class PlanLike
{
    [Column("plan_id")]
    public Guid PlanId { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
