using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacySubcategoryColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "subcategory",
                table: "places");

            migrationBuilder.DropColumn(
                name: "subcategory_i18n",
                table: "places");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "subcategory",
                table: "places",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "subcategory_i18n",
                table: "places",
                type: "jsonb",
                nullable: true);
        }
    }
}
