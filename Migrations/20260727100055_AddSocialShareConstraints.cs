using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialShareConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_user_blocks_no_self",
                table: "user_blocks",
                sql: "blocker_id <> blocked_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_plans_visibility_domain",
                table: "plans",
                sql: "visibility IN ('private', 'unlisted', 'public')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_user_blocks_no_self",
                table: "user_blocks");

            migrationBuilder.DropCheckConstraint(
                name: "ck_plans_visibility_domain",
                table: "plans");
        }
    }
}
