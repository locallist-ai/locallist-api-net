using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_plans_city_is_public",
                table: "plans");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<Guid>(
                name: "cloned_from",
                table: "plans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "likes_count",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "moderation_locked",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "published_at",
                table: "plans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "revision",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "share_token",
                table: "plans",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "visibility",
                table: "plans",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "private");

            // Backfill: la nueva columna se creo con default 'private' para TODAS las filas.
            // Deriva la visibilidad desde el flag legacy is_public: true -> 'public'. is_public=false
            // ya quedo correcto como 'private'. published_at se deja NULL en el backfill (los planes
            // legacy no tienen fecha de publicacion; el feed la usara solo para lo publicado en S1+).
            migrationBuilder.Sql("UPDATE plans SET visibility = 'public' WHERE is_public = true;");

            migrationBuilder.CreateTable(
                name: "activity_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    verb = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    object_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activity_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_activity_events_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "content_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_id = table.Column<Guid>(type: "uuid", nullable: true),
                    object_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    details = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_reports", x => x.id);
                    table.ForeignKey(
                        name: "FK_content_reports_users_reporter_id",
                        column: x => x.reporter_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "plan_collaborators",
                columns: table => new
                {
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    invited_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_collaborators", x => new { x.plan_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_plan_collaborators_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_plan_collaborators_users_invited_by",
                        column: x => x.invited_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_plan_collaborators_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plan_invites",
                columns: table => new
                {
                    token = table.Column<string>(type: "character varying(22)", maxLength: 22, nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    uses = table.Column<int>(type: "integer", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_invites", x => x.token);
                    table.ForeignKey(
                        name: "FK_plan_invites_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_plan_invites_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plan_likes",
                columns: table => new
                {
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_likes", x => new { x.plan_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_plan_likes_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_plan_likes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_blocks",
                columns: table => new
                {
                    blocker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocked_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_blocks", x => new { x.blocker_id, x.blocked_id });
                    table.ForeignKey(
                        name: "FK_user_blocks_users_blocked_id",
                        column: x => x.blocked_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_blocks_users_blocker_id",
                        column: x => x.blocker_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_follows",
                columns: table => new
                {
                    follower_id = table.Column<Guid>(type: "uuid", nullable: false),
                    followee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_follows", x => new { x.follower_id, x.followee_id });
                    table.CheckConstraint("ck_user_follows_no_self", "follower_id <> followee_id");
                    table.ForeignKey(
                        name: "FK_user_follows_users_followee_id",
                        column: x => x.followee_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_follows_users_follower_id",
                        column: x => x.follower_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_public_profiles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handle = table.Column<string>(type: "citext", maxLength: 30, nullable: false),
                    display_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    bio = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: true),
                    avatar_url = table.Column<string>(type: "text", nullable: true),
                    is_discoverable = table.Column<bool>(type: "boolean", nullable: false),
                    followers_count = table.Column<int>(type: "integer", nullable: false),
                    following_count = table.Column<int>(type: "integer", nullable: false),
                    public_plans_count = table.Column<int>(type: "integer", nullable: false),
                    show_favorites = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_public_profiles", x => x.user_id);
                    table.ForeignKey(
                        name: "FK_user_public_profiles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plans_city_visibility",
                table: "plans",
                columns: new[] { "city", "visibility" });

            migrationBuilder.CreateIndex(
                name: "IX_plans_cloned_from",
                table: "plans",
                column: "cloned_from");

            migrationBuilder.CreateIndex(
                name: "IX_plans_share_token",
                table: "plans",
                column: "share_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plans_visibility_published_at",
                table: "plans",
                columns: new[] { "visibility", "published_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_actor_id_created_at",
                table: "activity_events",
                columns: new[] { "actor_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_actor_id_verb_object_id",
                table: "activity_events",
                columns: new[] { "actor_id", "verb", "object_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_created_at",
                table: "activity_events",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_content_reports_reporter_id",
                table: "content_reports",
                column: "reporter_id");

            migrationBuilder.CreateIndex(
                name: "IX_content_reports_status_created_at",
                table: "content_reports",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_plan_collaborators_invited_by",
                table: "plan_collaborators",
                column: "invited_by");

            migrationBuilder.CreateIndex(
                name: "IX_plan_collaborators_user_id",
                table: "plan_collaborators",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_plan_invites_created_by",
                table: "plan_invites",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_plan_invites_plan_id",
                table: "plan_invites",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "IX_plan_likes_user_id_created_at",
                table: "plan_likes",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_user_blocks_blocked_id",
                table: "user_blocks",
                column: "blocked_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_follows_followee_id_created_at",
                table: "user_follows",
                columns: new[] { "followee_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_user_follows_follower_id_created_at",
                table: "user_follows",
                columns: new[] { "follower_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_user_public_profiles_handle",
                table: "user_public_profiles",
                column: "handle",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_plans_plans_cloned_from",
                table: "plans",
                column: "cloned_from",
                principalTable: "plans",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_plans_plans_cloned_from",
                table: "plans");

            migrationBuilder.DropTable(
                name: "activity_events");

            migrationBuilder.DropTable(
                name: "content_reports");

            migrationBuilder.DropTable(
                name: "plan_collaborators");

            migrationBuilder.DropTable(
                name: "plan_invites");

            migrationBuilder.DropTable(
                name: "plan_likes");

            migrationBuilder.DropTable(
                name: "user_blocks");

            migrationBuilder.DropTable(
                name: "user_follows");

            migrationBuilder.DropTable(
                name: "user_public_profiles");

            migrationBuilder.DropIndex(
                name: "IX_plans_city_visibility",
                table: "plans");

            migrationBuilder.DropIndex(
                name: "IX_plans_cloned_from",
                table: "plans");

            migrationBuilder.DropIndex(
                name: "IX_plans_share_token",
                table: "plans");

            migrationBuilder.DropIndex(
                name: "IX_plans_visibility_published_at",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "cloned_from",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "likes_count",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "moderation_locked",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "published_at",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "share_token",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "visibility",
                table: "plans");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateIndex(
                name: "IX_plans_city_is_public",
                table: "plans",
                columns: new[] { "city", "is_public" });
        }
    }
}
