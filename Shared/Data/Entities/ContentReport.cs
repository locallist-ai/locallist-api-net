using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

/// <summary>
/// Reporte de contenido para moderacion (S1+). reporter_id FK users SET NULL (el reporte sobrevive
/// al borrado de la cuenta del reportante). object_id polimorfico (plan/profile), SIN FK dura.
/// </summary>
[Table("content_reports")]
public class ContentReport
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("reporter_id")]
    public Guid? ReporterId { get; set; }

    /// <summary>'plan' | 'profile'.</summary>
    [Column("object_type")]
    [StringLength(20)]
    [Required]
    public string ObjectType { get; set; } = string.Empty;

    [Column("object_id")]
    public Guid ObjectId { get; set; }

    [Column("reason")]
    [StringLength(30)]
    [Required]
    public string Reason { get; set; } = string.Empty;

    [Column("details")]
    [StringLength(500)]
    public string? Details { get; set; }

    /// <summary>'open' | 'resolved' | 'dismissed'.</summary>
    [Column("status")]
    [StringLength(20)]
    [Required]
    public string Status { get; set; } = "open";

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("resolved_by")]
    public Guid? ResolvedBy { get; set; }

    [Column("resolved_at")]
    public DateTimeOffset? ResolvedAt { get; set; }
}
