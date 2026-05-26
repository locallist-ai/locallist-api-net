using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaceSubcategoriesArray : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "subcategories",
                table: "places",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "subcategories_i18n",
                table: "places",
                type: "jsonb",
                nullable: true);

            // Backfill: copy legacy single subcategory into new array column.
            migrationBuilder.Sql(@"
                UPDATE places
                   SET subcategories = ARRAY[subcategory]
                 WHERE subcategory IS NOT NULL
                   AND subcategory <> ''
                   AND subcategories IS NULL;

                UPDATE places
                   SET subcategories_i18n = jsonb_build_object('es', jsonb_build_array(subcategory_i18n->>'es'))
                 WHERE subcategory_i18n IS NOT NULL
                   AND subcategory_i18n ? 'es'
                   AND subcategories_i18n IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "subcategories",
                table: "places");

            migrationBuilder.DropColumn(
                name: "subcategories_i18n",
                table: "places");
        }
    }
}
