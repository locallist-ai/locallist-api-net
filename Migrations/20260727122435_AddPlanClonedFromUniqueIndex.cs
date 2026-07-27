using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanClonedFromUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_plans_created_by_cloned_from",
                table: "plans",
                columns: new[] { "created_by", "cloned_from" },
                unique: true,
                filter: "cloned_from IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_plans_created_by_cloned_from",
                table: "plans");
        }
    }
}
