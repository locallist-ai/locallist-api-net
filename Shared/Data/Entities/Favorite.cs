using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

/// <summary>
/// Favorito de un usuario sobre un <see cref="Place"/> (F-BE del build-out post-1.0; v1 = solo
/// sitios, no planes). PK compuesta <c>(user_id, place_id)</c> — es a la vez la clave y el índice
/// único que hace la operación de favoritar IDEMPOTENTE (segundo favoritar del mismo par cae en el
/// 23505 del índice, que el controller traga; mismo patrón que <c>SaveLedgerAsync</c> de billing).
/// Ambos FK con ON DELETE CASCADE: borrar la cuenta (GDPR) o el place arrastra sus favoritos.
///
/// El cap (50 free · ilimitado Plus) NO vive en esta entidad: se aplica en <c>FavoritesController</c>
/// con un lock consultivo por usuario (<c>pg_advisory_xact_lock</c>) que serializa los favoritos
/// concurrentes del MISMO usuario, de modo que el conteo del gate es exacto y dos requests
/// simultáneas en 49 no pueden dejar 51 (a diferencia de <c>usage_counters</c>, aquí no hay una
/// única fila-contador cuyo row-lock sirva — cada favorito es una fila distinta).
/// </summary>
[Table("favorites")]
public class Favorite
{
    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("place_id")]
    public Guid PlaceId { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
