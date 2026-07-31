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

    // Reuse detection: NULL = active (never rotated); non-NULL = already
    // rotated/invalidated. Rotated rows are RETAINED (not hard-deleted) so a
    // replay of a spent token can be distinguished from a token that never
    // existed → triggers token-family revocation. Pruned once past ExpiresAt.
    [Column("rotated_at")]
    public DateTimeOffset? RotatedAt { get; set; }

    public User? User { get; set; }
}
