using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

/// <summary>
/// Bloqueo entre usuarios. PK compuesta (blocker_id, blocked_id). Ambos FK CASCADE.
/// Consumido por PlanAccessService: un bloqueo entre el visor y el owner niega CanView (salvo al
/// propio owner, que siempre ve su plan).
/// </summary>
[Table("user_blocks")]
public class UserBlock
{
    [Column("blocker_id")]
    public Guid BlockerId { get; set; }

    [Column("blocked_id")]
    public Guid BlockedId { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
