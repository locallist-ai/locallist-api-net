using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddChatTurnsAndPlanMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_turns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    turn_index = table.Column<int>(type: "integer", nullable: false),
                    trace_id = table.Column<string>(type: "text", nullable: true),
                    ai_provider = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    prompt_version = table.Column<string>(type: "text", nullable: false),
                    user_message = table.Column<string>(type: "text", nullable: true),
                    quick_reply_id = table.Column<string>(type: "text", nullable: true),
                    context_signals = table.Column<string>(type: "jsonb", nullable: true),
                    prompt_chars = table.Column<int>(type: "integer", nullable: false),
                    prompt_excerpt = table.Column<string>(type: "text", nullable: false),
                    response_raw = table.Column<string>(type: "text", nullable: true),
                    finish_reason = table.Column<string>(type: "text", nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_tokens = table.Column<int>(type: "integer", nullable: true),
                    thinking_tokens = table.Column<int>(type: "integer", nullable: true),
                    total_tokens = table.Column<int>(type: "integer", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric", nullable: true),
                    gemini_status = table.Column<int>(type: "integer", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    slot_completeness = table.Column<short>(type: "smallint", nullable: true),
                    regenerated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_turns", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_turns_chat_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "chat_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_chat_turns_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "plan_metrics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chat_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    generate_turn_id = table.Column<Guid>(type: "uuid", nullable: true),
                    generation_source = table.Column<string>(type: "text", nullable: false),
                    signals_filled = table.Column<short>(type: "smallint", nullable: false),
                    num_days = table.Column<int>(type: "integer", nullable: false),
                    num_stops = table.Column<int>(type: "integer", nullable: false),
                    num_categories = table.Column<int>(type: "integer", nullable: false),
                    group_type = table.Column<string>(type: "text", nullable: true),
                    budget = table.Column<string>(type: "text", nullable: true),
                    vibes_json = table.Column<string>(type: "jsonb", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric", nullable: true),
                    was_opened = table.Column<bool>(type: "boolean", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    was_followed = table.Column<bool>(type: "boolean", nullable: false),
                    followed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    edited_count = table.Column<int>(type: "integer", nullable: false),
                    regenerated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_metrics", x => x.id);
                    table.ForeignKey(
                        name: "FK_plan_metrics_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_turns_created_at",
                table: "chat_turns",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_chat_turns_prompt_version_created_at",
                table: "chat_turns",
                columns: new[] { "prompt_version", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_turns_session_id_turn_index",
                table: "chat_turns",
                columns: new[] { "session_id", "turn_index" },
                unique: true,
                filter: "session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_chat_turns_user_id_created_at",
                table: "chat_turns",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_plan_metrics_created_at",
                table: "plan_metrics",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_plan_metrics_generation_source_created_at",
                table: "plan_metrics",
                columns: new[] { "generation_source", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_plan_metrics_plan_id",
                table: "plan_metrics",
                column: "plan_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plan_metrics_prompt_version_created_at",
                table: "plan_metrics",
                columns: new[] { "prompt_version", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_turns");

            migrationBuilder.DropTable(
                name: "plan_metrics");
        }
    }
}
