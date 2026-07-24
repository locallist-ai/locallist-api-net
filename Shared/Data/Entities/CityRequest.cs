using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

/// <summary>
/// Petición de cobertura de ciudad — feedback de cliente desde el "¿No ves tu
/// ciudad?" del selector. Texto libre (máx 100) que el usuario teclea para
/// pedirnos una ciudad que aún no cubrimos.
///
/// Es un dato INERTE: nunca se interpreta ni se renderiza. La defensa XSS real
/// es la validación de dominio en el controller (solo caracteres de nombre de
/// ciudad) + output encoding cuando exista la vista admin. <see cref="NormalizedCity"/>
/// (lowercase/trim/sin diacríticos vía <c>CityNameNormalizer</c>) agrupa variantes
/// ("Málaga" y "malaga" → "malaga") para el futuro "top ciudades pedidas".
///
/// <see cref="UserId"/> es nullable con FK SET NULL: el peticionario puede ser
/// invitado (anónimo) y la petición sobrevive al borrado de su cuenta (es
/// feedback de negocio). Mismo patrón que <see cref="ChatSession"/>.
/// </summary>
[Table("city_requests")]
public class CityRequest
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [Column("city_text")]
    [StringLength(100)]
    public string CityText { get; set; } = string.Empty;

    [Required]
    [Column("normalized_city")]
    [StringLength(120)]
    public string NormalizedCity { get; set; } = string.Empty;

    [Column("user_id")]
    public Guid? UserId { get; set; }

    [Column("ip_hash")]
    [StringLength(64)]
    public string? IpHash { get; set; }

    [Column("user_agent")]
    [StringLength(256)]
    public string? UserAgent { get; set; }

    [Column("locale")]
    [StringLength(16)]
    public string? Locale { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User? User { get; set; }
}
