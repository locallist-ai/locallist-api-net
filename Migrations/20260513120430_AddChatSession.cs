using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddChatSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    anonymous_ip_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_turn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    turn_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    slots = table.Column<string>(type: "text", nullable: false),
                    history = table.Column<string>(type: "text", nullable: false),
                    generated_plan_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_sessions_anonymous_ip_hash",
                table: "chat_sessions",
                column: "anonymous_ip_hash");

            migrationBuilder.CreateIndex(
                name: "IX_chat_sessions_last_turn_at",
                table: "chat_sessions",
                column: "last_turn_at");

            migrationBuilder.CreateIndex(
                name: "IX_chat_sessions_user_id_status",
                table: "chat_sessions",
                columns: new[] { "user_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_sessions");
        }
    }
}
