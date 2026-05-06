using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    public partial class AddWaitlistTracking : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE waitlist_entries
                    ADD COLUMN IF NOT EXISTS utm_source    character varying(100),
                    ADD COLUMN IF NOT EXISTS utm_medium    character varying(100),
                    ADD COLUMN IF NOT EXISTS utm_campaign  character varying(100),
                    ADD COLUMN IF NOT EXISTS utm_content   character varying(100),
                    ADD COLUMN IF NOT EXISTS utm_term      character varying(100),
                    ADD COLUMN IF NOT EXISTS referrer      character varying(500),
                    ADD COLUMN IF NOT EXISTS landing_path  character varying(500),
                    ADD COLUMN IF NOT EXISTS ip_hash       character varying(64),
                    ADD COLUMN IF NOT EXISTS user_agent    character varying(500),
                    ADD COLUMN IF NOT EXISTS ttclid        character varying(200),
                    ADD COLUMN IF NOT EXISTS fbclid        character varying(200),
                    ADD COLUMN IF NOT EXISTS gclid         character varying(200),
                    ADD COLUMN IF NOT EXISTS first_touch_at timestamp with time zone,
                    ADD COLUMN IF NOT EXISTS last_touch_at  timestamp with time zone;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE waitlist_entries
                    DROP COLUMN IF EXISTS utm_source,
                    DROP COLUMN IF EXISTS utm_medium,
                    DROP COLUMN IF EXISTS utm_campaign,
                    DROP COLUMN IF EXISTS utm_content,
                    DROP COLUMN IF EXISTS utm_term,
                    DROP COLUMN IF EXISTS referrer,
                    DROP COLUMN IF EXISTS landing_path,
                    DROP COLUMN IF EXISTS ip_hash,
                    DROP COLUMN IF EXISTS user_agent,
                    DROP COLUMN IF EXISTS ttclid,
                    DROP COLUMN IF EXISTS fbclid,
                    DROP COLUMN IF EXISTS gclid,
                    DROP COLUMN IF EXISTS first_touch_at,
                    DROP COLUMN IF EXISTS last_touch_at;
                """);
        }
    }
}
