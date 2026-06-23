using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyBestTimeColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "best_time",
                table: "places");

            migrationBuilder.DropColumn(
                name: "best_time_i18n",
                table: "places");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "best_time",
                table: "places",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "best_time_i18n",
                table: "places",
                type: "jsonb",
                nullable: true);
        }
    }
}
