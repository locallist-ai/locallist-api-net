using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Creates the full baseline schema using idempotent raw SQL (IF NOT EXISTS).
    /// Safe for both production (tables pre-exist) and CI/test (blank DB).
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS users (
                    id uuid NOT NULL,
                    email character varying(255) NOT NULL,
                    name character varying(255),
                    image text,
                    tier character varying(20) NOT NULL,
                    role character varying(20) NOT NULL,
                    password_hash character varying(255),
                    apple_user_id character varying(255),
                    google_user_id character varying(255),
                    firebase_uid character varying(128),
                    rc_customer_id character varying(255),
                    city character varying(100),
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_users" PRIMARY KEY (id)
                );
                """);

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS places (
                    id uuid NOT NULL,
                    name character varying(255) NOT NULL,
                    category character varying(50) NOT NULL,
                    subcategory character varying(100),
                    neighborhood character varying(100),
                    city character varying(100) NOT NULL,
                    latitude numeric(10,7),
                    longitude numeric(10,7),
                    why_this_place text NOT NULL,
                    best_for text[],
                    suitable_for text[],
                    best_time character varying(50),
                    price_range character varying(10),
                    photos text[],
                    opening_hours jsonb,
                    google_place_id character varying(255),
                    google_rating numeric(2,1),
                    google_review_count integer,
                    source character varying(50) NOT NULL,
                    source_url text,
                    status character varying(20) NOT NULL,
                    rejection_reason character varying(1000),
                    ai_vibe_score integer,
                    flags text[],
                    submitted_by uuid,
                    reviewed_by uuid,
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_places" PRIMARY KEY (id),
                    CONSTRAINT "FK_places_users_reviewed_by" FOREIGN KEY (reviewed_by)
                        REFERENCES users (id) ON DELETE SET NULL,
                    CONSTRAINT "FK_places_users_submitted_by" FOREIGN KEY (submitted_by)
                        REFERENCES users (id) ON DELETE SET NULL
                );
                """);

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS plans (
                    id uuid NOT NULL,
                    name character varying(255) NOT NULL,
                    city character varying(100) NOT NULL,
                    type character varying(20) NOT NULL,
                    description text,
                    image_url text,
                    duration_days integer NOT NULL,
                    trip_context jsonb,
                    is_public boolean NOT NULL,
                    is_showcase boolean NOT NULL,
                    created_by uuid,
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_plans" PRIMARY KEY (id),
                    CONSTRAINT "FK_plans_users_created_by" FOREIGN KEY (created_by)
                        REFERENCES users (id) ON DELETE SET NULL
                );
                """);

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS follow_sessions (
                    id uuid NOT NULL,
                    user_id uuid NOT NULL,
                    plan_id uuid NOT NULL,
                    status character varying(20) NOT NULL,
                    current_day_index integer NOT NULL,
                    current_stop_index integer NOT NULL,
                    started_at timestamp with time zone NOT NULL,
                    completed_at timestamp with time zone,
                    last_active_at timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_follow_sessions" PRIMARY KEY (id),
                    CONSTRAINT "FK_follow_sessions_plans_plan_id" FOREIGN KEY (plan_id)
                        REFERENCES plans (id) ON DELETE CASCADE,
                    CONSTRAINT "FK_follow_sessions_users_user_id" FOREIGN KEY (user_id)
                        REFERENCES users (id) ON DELETE CASCADE
                );
                """);

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS plan_stops (
                    id uuid NOT NULL,
                    plan_id uuid NOT NULL,
                    place_id uuid NOT NULL,
                    day_number integer NOT NULL,
                    order_index integer NOT NULL,
                    time_block character varying(20),
                    suggested_arrival interval,
                    suggested_duration_min integer,
                    travel_from_previous jsonb,
                    created_at timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_plan_stops" PRIMARY KEY (id),
                    CONSTRAINT "FK_plan_stops_places_place_id" FOREIGN KEY (place_id)
                        REFERENCES places (id) ON DELETE RESTRICT,
                    CONSTRAINT "FK_plan_stops_plans_plan_id" FOREIGN KEY (plan_id)
                        REFERENCES plans (id) ON DELETE CASCADE
                );
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_follow_sessions_plan_id" ON follow_sessions (plan_id);
                CREATE INDEX IF NOT EXISTS "IX_follow_sessions_user_id_status" ON follow_sessions (user_id, status);
                CREATE INDEX IF NOT EXISTS "IX_places_category" ON places (category);
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_places_google_place_id" ON places (google_place_id);
                CREATE INDEX IF NOT EXISTS "IX_places_reviewed_by" ON places (reviewed_by);
                CREATE INDEX IF NOT EXISTS "IX_places_status_city" ON places (status, city);
                CREATE INDEX IF NOT EXISTS "IX_places_submitted_by" ON places (submitted_by);
                CREATE INDEX IF NOT EXISTS "IX_plan_stops_place_id" ON plan_stops (place_id);
                CREATE INDEX IF NOT EXISTS "IX_plan_stops_plan_id_day_number" ON plan_stops (plan_id, day_number);
                CREATE INDEX IF NOT EXISTS "IX_plans_city_is_public" ON plans (city, is_public);
                CREATE INDEX IF NOT EXISTS "IX_plans_created_by" ON plans (created_by);
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_users_apple_user_id" ON users (apple_user_id);
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_users_email" ON users (email);
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_users_firebase_uid" ON users (firebase_uid);
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_users_google_user_id" ON users (google_user_id);
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_users_rc_customer_id" ON users (rc_customer_id);
                """);
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
