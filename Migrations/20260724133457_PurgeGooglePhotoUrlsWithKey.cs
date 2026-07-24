using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <summary>
    /// Limpieza de seguridad (cierra MINOR-2 del review de T3): antes de T3, algunas filas de
    /// <c>places</c> pudieron persistir URLs <c>places.googleapis.com/.../media?...key=SECRET</c>
    /// en <c>photos</c> (columna <c>text[]</c>, ver <see cref="LocalList.API.NET.Shared.Data.Entities.Place.Photos"/>).
    /// T2 ya nunca reemite esas URLs en READ (<c>PlacePhotoUrls.Resolve</c> las filtra
    /// defensivamente) y T3 ya nunca las escribe (<c>PlacePhotoUrls.SanitizeForStorage</c>), pero
    /// el secreto seguía en reposo en Postgres para las filas escritas antes de ese fix.
    /// </summary>
    /// <inheritdoc />
    public partial class PurgeGooglePhotoUrlsWithKey : Migration
    {
        /// <summary>
        /// Elimina de cada array <c>photos</c> SOLO los elementos que contienen
        /// <c>googleapis.com</c> (los que llevan la key en el query string), conservando
        /// cualquier URL externa legítima (Yelp, creadores, etc.) que conviva en el mismo
        /// array. <c>array_agg</c> sobre un conjunto vacío devuelve NULL de forma nativa en
        /// Postgres, así que si tras el filtrado no queda ningún elemento el resultado ya es
        /// NULL (coherente con la semántica "sin fotos = null" del resto del código, ver
        /// <c>PlacePhotoUrls.SanitizeForStorage</c>). <c>unnest</c> sobre un array preserva el
        /// orden original, así que las URLs externas supervivientes mantienen su orden. El
        /// WHERE limita el UPDATE a las filas realmente afectadas (idempotente: una fila ya
        /// limpia, o sin fotos, no entra en el WHERE y no se toca en re-ejecuciones).
        /// </summary>
        internal const string PurgeGoogleKeyedPhotosSql = """
            UPDATE places
            SET photos = (
                SELECT array_agg(elem ORDER BY ord)
                FROM unnest(photos) WITH ORDINALITY AS t(elem, ord)
                WHERE elem NOT ILIKE '%googleapis.com%'
            )
            WHERE photos IS NOT NULL
              AND EXISTS (
                SELECT 1 FROM unnest(photos) AS elem WHERE elem ILIKE '%googleapis.com%'
              );
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PurgeGoogleKeyedPhotosSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op intencional: esta migración purga un secreto (API key de Google
            // persistido en `photos`) que no tenemos guardado en ningún otro sitio y que no
            // queremos reconstruir. Es una limpieza de seguridad, no reversible por diseño.
        }
    }
}
