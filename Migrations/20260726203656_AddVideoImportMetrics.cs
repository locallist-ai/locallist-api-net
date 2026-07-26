using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoImportMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "video_import_metrics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: true),
                    model = table.Column<string>(type: "text", nullable: false),
                    ai_provider = table.Column<string>(type: "text", nullable: false),
                    mime_type = table.Column<string>(type: "text", nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    duration_sec = table.Column<double>(type: "double precision", nullable: true),
                    caption_provided = table.Column<bool>(type: "boolean", nullable: false),
                    city = table.Column<string>(type: "text", nullable: true),
                    country = table.Column<string>(type: "text", nullable: true),
                    language = table.Column<string>(type: "text", nullable: true),
                    num_places = table.Column<int>(type: "integer", nullable: false),
                    num_places_dropped = table.Column<int>(type: "integer", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_tokens = table.Column<int>(type: "integer", nullable: true),
                    thinking_tokens = table.Column<int>(type: "integer", nullable: true),
                    total_tokens = table.Column<int>(type: "integer", nullable: true),
                    estimated_media_tokens = table.Column<int>(type: "integer", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric", nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    finish_reason = table.Column<string>(type: "text", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_import_metrics", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_video_import_metrics_created_at",
                table: "video_import_metrics",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_video_import_metrics_platform_created_at",
                table: "video_import_metrics",
                columns: new[] { "platform", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "video_import_metrics");
        }
    }
}
