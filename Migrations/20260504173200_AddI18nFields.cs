using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddI18nFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<JsonDocument>(
                name: "description_i18n",
                table: "plans",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "name_i18n",
                table: "plans",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "best_for_i18n",
                table: "places",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "best_time_i18n",
                table: "places",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "name_i18n",
                table: "places",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "neighborhood_i18n",
                table: "places",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "subcategory_i18n",
                table: "places",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "suitable_for_i18n",
                table: "places",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "translation_status",
                table: "places",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "why_this_place_i18n",
                table: "places",
                type: "jsonb",
                nullable: true);

            // Backfill EN values from existing string columns (idempotent)
            migrationBuilder.Sql(@"
                UPDATE places SET name_i18n = jsonb_build_object('en', name)
                    WHERE name_i18n IS NULL AND name IS NOT NULL;

                UPDATE places SET why_this_place_i18n = jsonb_build_object('en', why_this_place)
                    WHERE why_this_place_i18n IS NULL AND why_this_place IS NOT NULL;

                UPDATE places SET best_time_i18n = jsonb_build_object('en', best_time)
                    WHERE best_time_i18n IS NULL AND best_time IS NOT NULL;

                UPDATE places SET neighborhood_i18n = jsonb_build_object('en', neighborhood)
                    WHERE neighborhood_i18n IS NULL AND neighborhood IS NOT NULL;

                UPDATE places SET subcategory_i18n = jsonb_build_object('en', subcategory)
                    WHERE subcategory_i18n IS NULL AND subcategory IS NOT NULL;

                UPDATE places SET best_for_i18n = jsonb_build_object('en', to_json(best_for)::jsonb)
                    WHERE best_for_i18n IS NULL AND best_for IS NOT NULL;

                UPDATE places SET suitable_for_i18n = jsonb_build_object('en', to_json(suitable_for)::jsonb)
                    WHERE suitable_for_i18n IS NULL AND suitable_for IS NOT NULL;

                UPDATE plans SET name_i18n = jsonb_build_object('en', name)
                    WHERE name_i18n IS NULL AND name IS NOT NULL;

                UPDATE plans SET description_i18n = jsonb_build_object('en', description)
                    WHERE description_i18n IS NULL AND description IS NOT NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "description_i18n",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "name_i18n",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "best_for_i18n",
                table: "places");

            migrationBuilder.DropColumn(
                name: "best_time_i18n",
                table: "places");

            migrationBuilder.DropColumn(
                name: "name_i18n",
                table: "places");

            migrationBuilder.DropColumn(
                name: "neighborhood_i18n",
                table: "places");

            migrationBuilder.DropColumn(
                name: "subcategory_i18n",
                table: "places");

            migrationBuilder.DropColumn(
                name: "suitable_for_i18n",
                table: "places");

            migrationBuilder.DropColumn(
                name: "translation_status",
                table: "places");

            migrationBuilder.DropColumn(
                name: "why_this_place_i18n",
                table: "places");
        }
    }
}
