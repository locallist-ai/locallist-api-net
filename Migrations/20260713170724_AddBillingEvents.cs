using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rc_event_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    app_user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_timestamp_ms = table.Column<long>(type: "bigint", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_events_rc_event_id",
                table: "billing_events",
                column: "rc_event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_billing_events_user_id_event_timestamp_ms",
                table: "billing_events",
                columns: new[] { "user_id", "event_timestamp_ms" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_events");
        }
    }
}
