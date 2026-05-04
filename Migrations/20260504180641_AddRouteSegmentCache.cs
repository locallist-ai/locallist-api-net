using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddRouteSegmentCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS route_segment_cache (
                    id uuid NOT NULL,
                    from_place_id uuid NOT NULL,
                    to_place_id uuid NOT NULL,
                    mode character varying(20) NOT NULL,
                    encoded_polyline text NOT NULL,
                    distance_meters integer NOT NULL,
                    duration_seconds integer NOT NULL,
                    computed_at timestamp with time zone NOT NULL DEFAULT NOW(),
                    CONSTRAINT "PK_route_segment_cache" PRIMARY KEY (id),
                    CONSTRAINT "FK_route_segment_cache_from_place"
                        FOREIGN KEY (from_place_id) REFERENCES places(id) ON DELETE CASCADE,
                    CONSTRAINT "FK_route_segment_cache_to_place"
                        FOREIGN KEY (to_place_id) REFERENCES places(id) ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_route_segment_cache_from_to_mode"
                    ON route_segment_cache (from_place_id, to_place_id, mode);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "route_segment_cache");
        }
    }
}
