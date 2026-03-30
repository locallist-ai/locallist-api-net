using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Baseline migration — schema already exists in production.
    /// Up/Down intentionally empty. EF uses this to establish the migration history table.
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — schema already exists in production DB.
            // This migration establishes the __EFMigrationsHistory baseline.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — not safe to drop the entire schema.
        }
    }
}
