using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Features.Social.Entities;

/// <summary>
/// Invitacion por token a colaborar en un plan (S1+). token (22 chars) es la PK. plan_id y
/// created_by FK CASCADE. Soporta expiracion, tope de usos y revocacion.
/// </summary>
[Table("plan_invites")]
public class PlanInvite
{
    [Key]
    [Column("token")]
    [StringLength(22)]
    public string Token { get; set; } = string.Empty;

    [Column("plan_id")]
    public Guid PlanId { get; set; }

    /// <summary>'editor' | 'viewer'.</summary>
    [Column("role")]
    [StringLength(10)]
    [Required]
    public string Role { get; set; } = "viewer";

    [Column("created_by")]
    public Guid CreatedBy { get; set; }

    [Column("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    [Column("max_uses")]
    public int? MaxUses { get; set; }

    [Column("uses")]
    public int Uses { get; set; } = 0;

    [Column("revoked_at")]
    public DateTimeOffset? RevokedAt { get; set; }
}
