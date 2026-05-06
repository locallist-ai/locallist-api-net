using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddCitiesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS cities (
                    id uuid NOT NULL,
                    name character varying(60) NOT NULL,
                    normalized_name character varying(60) NOT NULL,
                    country character varying(60),
                    source character varying(20) NOT NULL,
                    created_by_id uuid,
                    created_at timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_cities" PRIMARY KEY (id)
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_cities_normalized_name"
                    ON cities (normalized_name);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS cities;
                """);
        }
    }
}
