using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    public partial class FixPlanSourceCurated : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // All plans created before the source column existed were curated (admin-only tool).
            // User-generated plans don't exist yet, so backfill all 'user' rows to 'curated'.
            migrationBuilder.Sql("""
                UPDATE plans SET source = 'curated' WHERE source = 'user';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE plans SET source = 'user' WHERE source = 'curated';
                """);
        }
    }
}
