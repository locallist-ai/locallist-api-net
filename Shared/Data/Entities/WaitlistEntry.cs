using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

[Table("waitlist_entries")]
public class WaitlistEntry
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [Column("email")]
    [StringLength(255)]
    public string Email { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("utm_source")]
    [StringLength(100)]
    public string? UtmSource { get; set; }

    [Column("utm_medium")]
    [StringLength(100)]
    public string? UtmMedium { get; set; }

    [Column("utm_campaign")]
    [StringLength(100)]
    public string? UtmCampaign { get; set; }

    [Column("utm_content")]
    [StringLength(100)]
    public string? UtmContent { get; set; }

    [Column("utm_term")]
    [StringLength(100)]
    public string? UtmTerm { get; set; }

    [Column("referrer")]
    [StringLength(500)]
    public string? Referrer { get; set; }

    [Column("landing_path")]
    [StringLength(500)]
    public string? LandingPath { get; set; }

    [Column("ip_hash")]
    [StringLength(64)]
    public string? IpHash { get; set; }

    [Column("user_agent")]
    [StringLength(500)]
    public string? UserAgent { get; set; }

    [Column("ttclid")]
    [StringLength(200)]
    public string? Ttclid { get; set; }

    [Column("fbclid")]
    [StringLength(200)]
    public string? Fbclid { get; set; }

    [Column("gclid")]
    [StringLength(200)]
    public string? Gclid { get; set; }

    [Column("first_touch_at")]
    public DateTimeOffset? FirstTouchAt { get; set; }

    [Column("last_touch_at")]
    public DateTimeOffset? LastTouchAt { get; set; }

    [Column("anonymous_id")]
    [StringLength(64)]
    public string? AnonymousId { get; set; }
}
