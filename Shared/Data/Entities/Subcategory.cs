using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

[Table("subcategories")]
public class Subcategory
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("category_key")]
    [StringLength(50)]
    [Required]
    public string CategoryKey { get; set; } = string.Empty;

    [Column("key")]
    [StringLength(80)]
    [Required]
    public string Key { get; set; } = string.Empty;

    [Column("label_en")]
    [StringLength(120)]
    [Required]
    public string LabelEn { get; set; } = string.Empty;

    [Column("label_es")]
    [StringLength(120)]
    [Required]
    public string LabelEs { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("deleted_at")]
    public DateTimeOffset? DeletedAt { get; set; }

    [Column("created_by_admin_user_id")]
    public Guid? CreatedByAdminUserId { get; set; }
}
