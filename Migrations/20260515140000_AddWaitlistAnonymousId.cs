using LocalList.API.NET.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    [DbContext(typeof(LocalListDbContext))]
    [Migration("20260515140000_AddWaitlistAnonymousId")]
    public partial class AddWaitlistAnonymousId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "anonymous_id",
                table: "waitlist_entries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "anonymous_id",
                table: "waitlist_entries");
        }
    }
}
