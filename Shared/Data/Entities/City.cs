using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

/// <summary>
/// Registro de ciudades en el catálogo. Pablo 2026-04-27: el manual builder
/// permite teclear cualquier ciudad; cuando es una nueva (no existe en esta
/// tabla) se crea una entry y queda disponible para autocomplete + futuras
/// consultas. <c>Plan.City</c> sigue siendo string libre por compatibilidad
/// histórica — la relación es lookup, no FK.
/// </summary>
[Table("cities")]
public class City
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("name")]
    [StringLength(60)]
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Versión normalizada (lowercase, sin acentos, sin trim) para unique
    /// constraint y matching en autocomplete. Evita duplicados como "Miami"
    /// vs "miami" o "Málaga" vs "Malaga".
    /// </summary>
    [Column("normalized_name")]
    [StringLength(60)]
    [Required]
    public string NormalizedName { get; set; } = string.Empty;

    [Column("country")]
    [StringLength(60)]
    public string? Country { get; set; }

    /// <summary>"seed" (semilla del catálogo curado) o "user" (añadida por usuario).</summary>
    [Column("source")]
    [StringLength(20)]
    [Required]
    public string Source { get; set; } = "user";

    [Column("created_by_id")]
    public Guid? CreatedById { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
