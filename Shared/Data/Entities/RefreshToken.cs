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

    // Chain link set ONLY on legit single-use rotation: A→B stores A.ReplacedById = B.Id.
    // The past-grace theft discriminator (RFC 6819): a re-presented spent token is only
    // treated as reuse if its direct successor was itself CONSUMED (the chain advanced
    // beyond it) — evidence a second party used it. Benign recovery mints deliberately do
    // NOT set/advance this, so unlimited lost-response retries of a token whose successor
    // was never consumed stay graceful at any delay. Plain Guid (no FK) so pruning a
    // superseded row never trips a constraint.
    [Column("replaced_by_id")]
    public Guid? ReplacedById { get; set; }

    public User? User { get; set; }
}
