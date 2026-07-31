using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

[Table("refresh_tokens")]
public class RefreshToken
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("user_id")]
    [Required]
    public Guid UserId { get; set; }

    [Column("token_hash")]
    [StringLength(255)]
    [Required]
    public string TokenHash { get; set; } = string.Empty;

    [Column("token_prefix")]
    [StringLength(16)]
    [Required]
    public string TokenPrefix { get; set; } = string.Empty;

    [Column("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Reuse detection. Rotated rows are RETAINED (not hard-deleted) so a replay of a
    // spent token can be distinguished from a token that never existed. Three states:
    //   active  : RotatedAt == null && RevokedAt == null
    //   rotated : RotatedAt != null && RevokedAt == null  (superseded by normal
    //             single-use rotation; still GRACE-eligible for a lost-response retry)
    //   revoked : RevokedAt != null                       (killed by family revocation
    //             after reuse was detected; permanently dead, never grace-eligible)
    // Both columns are pruned once the row is past ExpiresAt.
    [Column("rotated_at")]
    public DateTimeOffset? RotatedAt { get; set; }

    [Column("revoked_at")]
    public DateTimeOffset? RevokedAt { get; set; }

    public User? User { get; set; }
}
