using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LocalList.API.NET.Shared.Data;

/// <summary>
/// Predicados compartidos para clasificar violaciones de constraint de Postgres capturadas en un
/// <see cref="DbUpdateException"/>. Viven en Shared porque más de un slice enruta la MISMA carrera
/// (Favorites e Import materializan filas cuyo FK a <c>places</c> puede romperse por un hard-delete
/// admin concurrente 23503; el índice único de favoritos usa 23505 para idempotencia). Antes cada
/// controller tenía su copia; centralizarlos evita la dependencia controller→controller cross-slice.
/// </summary>
public static class PostgresErrorPredicates
{
    /// <summary>Violación del índice único (SqlState "23505") — p.ej. insert idempotente duplicado.</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation;

    /// <summary>
    /// Violación de foreign key (SqlState "23503") — carrera con un hard-delete del referenciado
    /// entre el check y el insert. Se mapea a un 4xx opaco (nunca 500).
    /// </summary>
    public static bool IsForeignKeyViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.ForeignKeyViolation;
}
