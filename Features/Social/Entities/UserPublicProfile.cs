using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Social.Entities;

/// <summary>
/// Perfil PUBLICO del usuario (handle, avatar, contadores sociales). SEPARADO de
/// <see cref="UserProfile"/>, que es privado (preferencias de viaje). Creacion LAZY: la fila no
/// existe hasta que el usuario reclama un handle (S1). Aqui solo se define la tabla.
/// </summary>
[Table("user_public_profiles")]
public class UserPublicProfile
{
    [Key]
    [Column("user_id")]
    public Guid UserId { get; set; }

    /// <summary>3-30 chars, [a-z0-9_.], citext UNIQUE. Wordlist de reservados aplicada en S1 al reclamar.</summary>
    [Column("handle", TypeName = "citext")]
    [StringLength(30)]
    [Required]
    public string Handle { get; set; } = string.Empty;

    [Column("display_name")]
    [StringLength(50)]
    public string? DisplayName { get; set; }

    [Column("bio")]
    [StringLength(280)]
    public string? Bio { get; set; }

    [Column("avatar_url")]
    public string? AvatarUrl { get; set; }

    [Column("is_discoverable")]
    public bool IsDiscoverable { get; set; } = true;

    [Column("followers_count")]
    public int FollowersCount { get; set; } = 0;

    [Column("following_count")]
    public int FollowingCount { get; set; } = 0;

    [Column("public_plans_count")]
    public int PublicPlansCount { get; set; } = 0;

    /// <summary>RESERVADO para el carril favoritos: exponer o no la lista de favoritos en el perfil.</summary>
    [Column("show_favorites")]
    public bool ShowFavorites { get; set; } = false;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User? User { get; set; }
}
