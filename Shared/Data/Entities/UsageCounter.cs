using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

/// <summary>
/// Contador de consumo por usuario/feature/periodo (F4 — gates del catálogo Plus).
/// Una fila por (user, feature, inicio de periodo); el periodo lo decide el caller
/// (mes natural UTC para el límite free, día UTC para el cap antiabuso Plus) — la
/// tabla no lo interpreta. El increment es atómico vía upsert condicional en
/// <see cref="LocalList.API.NET.Shared.Usage.UsageCounterService"/>: NUNCA
/// escribir <see cref="Count"/> con read-modify-write de EF (dos requests
/// concurrentes podrían gastar el mismo permiso dos veces).
/// FK a users con cascade: borrar la cuenta (GDPR) borra sus contadores.
/// </summary>
[Table("usage_counters")]
public class UsageCounter
{
    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("feature")]
    [StringLength(50)]
    public string Feature { get; set; } = string.Empty;

    [Column("period_start")]
    public DateOnly PeriodStart { get; set; }

    [Column("count")]
    public int Count { get; set; }
}
