using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeEmailCitext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Higiene de email: normalizar (lower+trim) las filas existentes y pasar la columna
            // a citext (unicidad/igualdad case-insensitive a nivel de esquema).
            //
            // ORDEN CRÍTICO (todo en la transacción de la migración):
            //   1. GUARD: si al normalizar dos filas colisionarían, RAISE aborta ANTES de tocar
            //      nada. Con datos sucios el ALTER a citext fallaría a mitad con un 23505 opaco;
            //      preferimos abortar limpio y forzar resolución manual de los duplicados.
            //   2. UPDATE: canonicaliza los valores existentes (idempotente vía IS DISTINCT FROM).
            //   3. ALTER: convierte el tipo a citext → el índice único IX_users_email se reconstruye
            //      case-insensitive automáticamente.
            migrationBuilder.Sql("""
                DO $$
                DECLARE collisions int;
                BEGIN
                  SELECT count(*) INTO collisions FROM (
                    SELECT 1 FROM users GROUP BY lower(trim(email)) HAVING count(*) > 1
                  ) d;
                  IF collisions > 0 THEN
                    RAISE EXCEPTION 'Email normalization aborted: % colisiones normalizadas en users. Resolver manualmente antes de migrar.', collisions;
                  END IF;
                END $$;
                UPDATE users SET email = lower(trim(email)) WHERE email IS DISTINCT FROM lower(trim(email));
                ALTER TABLE users ALTER COLUMN email TYPE citext;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "email",
                table: "users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "citext",
                oldMaxLength: 255);
        }
    }
}
