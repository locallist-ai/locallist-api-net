using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Creates the full baseline schema. In production, this migration is marked as
    /// already applied (tables pre-exist). In CI/test, it creates all tables from scratch.
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    image = table.Column<string>(type: "text", nullable: true),
                    tier = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    apple_user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    google_user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    firebase_uid = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    rc_customer_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "places",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    subcategory = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    neighborhood = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    latitude = table.Column<decimal>(type: "numeric(10,7)", nullable: true),
                    longitude = table.Column<decimal>(type: "numeric(10,7)", nullable: true),
                    why_this_place = table.Column<string>(type: "text", nullable: false),
                    best_for = table.Column<List<string>>(type: "text[]", nullable: true),
                    suitable_for = table.Column<List<string>>(type: "text[]", nullable: true),
                    best_time = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    price_range = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    photos = table.Column<List<string>>(type: "text[]", nullable: true),
                    opening_hours = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    google_place_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    google_rating = table.Column<decimal>(type: "numeric(2,1)", nullable: true),
                    google_review_count = table.Column<int>(type: "integer", nullable: true),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    rejection_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ai_vibe_score = table.Column<int>(type: "integer", nullable: true),
                    flags = table.Column<List<string>>(type: "text[]", nullable: true),
                    submitted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_places", x => x.id);
                    table.ForeignKey(
                        name: "FK_places_users_reviewed_by",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_places_users_submitted_by",
                        column: x => x.submitted_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    image_url = table.Column<string>(type: "text", nullable: true),
                    duration_days = table.Column<int>(type: "integer", nullable: false),
                    trip_context = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    is_public = table.Column<bool>(type: "boolean", nullable: false),
                    is_showcase = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plans", x => x.id);
                    table.ForeignKey(
                        name: "FK_plans_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "follow_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    current_day_index = table.Column<int>(type: "integer", nullable: false),
                    current_stop_index = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_active_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_follow_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_follow_sessions_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_follow_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plan_stops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    place_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_number = table.Column<int>(type: "integer", nullable: false),
                    order_index = table.Column<int>(type: "integer", nullable: false),
                    time_block = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    suggested_arrival = table.Column<TimeSpan>(type: "interval", nullable: true),
                    suggested_duration_min = table.Column<int>(type: "integer", nullable: true),
                    travel_from_previous = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_stops", x => x.id);
                    table.ForeignKey(
                        name: "FK_plan_stops_places_place_id",
                        column: x => x.place_id,
                        principalTable: "places",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_plan_stops_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(name: "IX_follow_sessions_plan_id", table: "follow_sessions", column: "plan_id");
            migrationBuilder.CreateIndex(name: "IX_follow_sessions_user_id_status", table: "follow_sessions", columns: new[] { "user_id", "status" });
            migrationBuilder.CreateIndex(name: "IX_places_category", table: "places", column: "category");
            migrationBuilder.CreateIndex(name: "IX_places_google_place_id", table: "places", column: "google_place_id", unique: true);
            migrationBuilder.CreateIndex(name: "IX_places_reviewed_by", table: "places", column: "reviewed_by");
            migrationBuilder.CreateIndex(name: "IX_places_status_city", table: "places", columns: new[] { "status", "city" });
            migrationBuilder.CreateIndex(name: "IX_places_submitted_by", table: "places", column: "submitted_by");
            migrationBuilder.CreateIndex(name: "IX_plan_stops_place_id", table: "plan_stops", column: "place_id");
            migrationBuilder.CreateIndex(name: "IX_plan_stops_plan_id_day_number", table: "plan_stops", columns: new[] { "plan_id", "day_number" });
            migrationBuilder.CreateIndex(name: "IX_plans_city_is_public", table: "plans", columns: new[] { "city", "is_public" });
            migrationBuilder.CreateIndex(name: "IX_plans_created_by", table: "plans", column: "created_by");
            migrationBuilder.CreateIndex(name: "IX_users_apple_user_id", table: "users", column: "apple_user_id", unique: true);
            migrationBuilder.CreateIndex(name: "IX_users_email", table: "users", column: "email", unique: true);
            migrationBuilder.CreateIndex(name: "IX_users_firebase_uid", table: "users", column: "firebase_uid", unique: true);
            migrationBuilder.CreateIndex(name: "IX_users_google_user_id", table: "users", column: "google_user_id", unique: true);
            migrationBuilder.CreateIndex(name: "IX_users_rc_customer_id", table: "users", column: "rc_customer_id", unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "follow_sessions");
            migrationBuilder.DropTable(name: "plan_stops");
            migrationBuilder.DropTable(name: "places");
            migrationBuilder.DropTable(name: "plans");
            migrationBuilder.DropTable(name: "users");
        }
    }
}
