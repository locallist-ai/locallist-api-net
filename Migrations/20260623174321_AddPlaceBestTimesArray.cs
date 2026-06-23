using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaceBestTimesArray : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "best_times",
                table: "places",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "best_times_i18n",
                table: "places",
                type: "jsonb",
                nullable: true);

            // Backfill: copy legacy single best_time into new array column.
            migrationBuilder.Sql(@"
                UPDATE places
                   SET best_times = ARRAY[best_time]
                 WHERE best_time IS NOT NULL
                   AND best_time <> ''
                   AND best_times IS NULL;

                UPDATE places
                   SET best_times_i18n = jsonb_build_object('es', jsonb_build_array(best_time_i18n->>'es'))
                 WHERE best_time_i18n IS NOT NULL
                   AND best_time_i18n ? 'es'
                   AND best_times_i18n IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "best_times",
                table: "places");

            migrationBuilder.DropColumn(
                name: "best_times_i18n",
                table: "places");
        }
    }
}
