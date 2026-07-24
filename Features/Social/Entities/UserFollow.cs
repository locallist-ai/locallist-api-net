using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Features.Social.Entities;

/// <summary>
/// Grafo de follows (usuario sigue a usuario). Nombre NUNCA "Follow" — colisiona con Follow Mode.
/// PK compuesta (follower_id, followee_id), CHECK follower != followee, sin estado 'pending' en v1.
/// Ambos FK a users con CASCADE (borrado de cuenta cascadea el grafo — GDPR).
/// </summary>
[Table("user_follows")]
public class UserFollow
{
    [Column("follower_id")]
    public Guid FollowerId { get; set; }

    [Column("followee_id")]
    public Guid FolloweeId { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
