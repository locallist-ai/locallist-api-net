using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanTranslationStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE plans ADD COLUMN IF NOT EXISTS translation_status jsonb;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE plans DROP COLUMN IF EXISTS translation_status;
                """);
        }
    }
}
