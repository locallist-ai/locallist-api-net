using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace LocalList.API.NET.Shared.Data.Entities;

[Table("plans")]
public class Plan
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("name")]
    [StringLength(255)]
    [Required]
    public string Name { get; set; } = string.Empty;

    [Column("city")]
    [StringLength(100)]
    public string City { get; set; } = "Miami";

    [Column("type")]
    [StringLength(20)]
    [Required]
    public string Type { get; set; } = string.Empty;

    [Column("description")]
    public string? Description { get; set; }

    [Column("name_i18n", TypeName = "jsonb")]
    public JsonDocument? NameI18n { get; set; }

    [Column("description_i18n", TypeName = "jsonb")]
    public JsonDocument? DescriptionI18n { get; set; }

    [Column("translation_status", TypeName = "jsonb")]
    public JsonDocument? TranslationStatus { get; set; }

    [Column("image_url")]
    public string? ImageUrl { get; set; }

    [Column("duration_days")]
    public int DurationDays { get; set; } = 1;

    /// <summary>
    /// Fecha de inicio del viaje (fecha de calendario, sin zona horaria). Copiada desde
    /// TripContextDto.StartDate en la creacion (API-3). null = plan legacy sin fecha (compat
    /// total). Persistirla permite mostrar la fecha del viaje, derivar la fecha de cada dia
    /// (dia N = StartDate + (N-1)) y re-seguir/editar el plan con contexto temporal. Mapea a
    /// columna Postgres `date`. Se serializa como "yyyy-MM-dd", coherente con API-1.
    /// </summary>
    [Column("start_date")]
    public DateOnly? StartDate { get; set; }

    [Column("trip_context", TypeName = "jsonb")]
    public JsonDocument? TripContext { get; set; }

    // ── Visibilidad social (S0). `visibility` es la fuente de verdad para la autorizacion
    // (PlanAccessService la consume). `is_public` se mantiene sincronizada 1-2 releases porque
    // el DTO publico expone `isPublic` y la app vieja lo consume. Sincronizacion bidireccional a
    // nivel de setter: cualquier codigo legado que escriba IsPublic obtiene una Visibility
    // coherente, y el codigo nuevo (S1+) que escriba Visibility mantiene IsPublic coherente. Cada
    // setter solo toca el campo del OTRO lado de forma condicional, de modo que la materializacion
    // EF (que fija ambas columnas desde la fila, en orden no garantizado) converge al mismo estado
    // siempre que la fila sea coherente en BD (garantizado por el backfill de la migracion y por
    // estos setters). No usar backing field unico: perderia 'unlisted' segun el orden de set.
    private string _visibility = "public";
    private bool _isPublic = true;

    /// <summary>'private' | 'unlisted' | 'public'. Fuente de verdad de la autorizacion.</summary>
    [Column("visibility")]
    [StringLength(10)]
    [Required]
    public string Visibility
    {
        get => _visibility;
        set
        {
            _visibility = value;
            _isPublic = value == "public";
        }
    }

    /// <summary>Legacy mirror de <see cref="Visibility"/>. Mantener sincronizada (ver nota arriba).</summary>
    [Column("is_public")]
    public bool IsPublic
    {
        get => _isPublic;
        set
        {
            _isPublic = value;
            if (value) _visibility = "public";
            else if (_visibility == "public") _visibility = "private";
            // value==false y visibility ya no-publica ('unlisted'/'private'): se preserva.
        }
    }

    /// <summary>base62 corto (<=16), generado al primer share. NULL = plan nunca compartido.</summary>
    [Column("share_token")]
    [StringLength(16)]
    public string? ShareToken { get; set; }

    /// <summary>Concurrencia optimista para co-edicion (S1+). Se incrementa en cada mutacion de stops.</summary>
    [Column("revision")]
    public int Revision { get; set; } = 0;

    /// <summary>Contador denormalizado de likes. Se mantiene EN LA MISMA TRANSACCION (single-replica).</summary>
    [Column("likes_count")]
    public int LikesCount { get; set; } = 0;

    /// <summary>Cuando el plan paso a 'public' (ordena el feed). NULL si nunca publicado.</summary>
    [Column("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Origen del clone del share-link (S1). FK plans SET NULL.</summary>
    [Column("cloned_from")]
    public Guid? ClonedFrom { get; set; }

    /// <summary>El admin fuerza private y el owner no puede re-publicar mientras este true.</summary>
    [Column("moderation_locked")]
    public bool ModerationLocked { get; set; } = false;

    [Column("is_showcase")]
    public bool IsShowcase { get; set; } = false;

    [Column("source")]
    [StringLength(50)]
    public string Source { get; set; } = "user";

    // ── Atribución de import (F2 T4). Solo se pueblan cuando el plan nace de un import de vídeo
    // confirmado. NO confundir con `cloned_from` (eso es plan→plan del share-link). Un plan con
    // Source == "imported" NUNCA es curated (isCurated mira Source == "curated") ni showcase.
    /// <summary>Plataforma de origen del import (tiktok|instagram|other). NULL para self o no-import.</summary>
    [Column("imported_from_platform")]
    [StringLength(16)]
    public string? ImportedFromPlatform { get; set; }

    /// <summary>Handle del creador atribuido, SANEADO y sin '@' (regex tipo handle). NULL si ausente/inválido.</summary>
    [Column("imported_creator_handle")]
    [StringLength(64)]
    public string? ImportedCreatorHandle { get; set; }

    [Column("created_by")]
    public Guid? CreatedById { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User? CreatedBy { get; set; }
    public ICollection<PlanStop> Stops { get; set; } = new List<PlanStop>();
    public ICollection<FollowSession> FollowSessions { get; set; } = new List<FollowSession>();
}
