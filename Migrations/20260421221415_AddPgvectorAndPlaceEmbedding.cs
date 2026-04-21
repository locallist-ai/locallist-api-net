using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddPgvectorAndPlaceEmbedding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE EXTENSION IF NOT EXISTS vector;

                ALTER TABLE places
                    ADD COLUMN IF NOT EXISTS embedding vector(768);

                CREATE INDEX IF NOT EXISTS "IX_places_embedding_hnsw"
                    ON places
                    USING hnsw (embedding vector_cosine_ops)
                    WITH (m = 16, ef_construction = 64);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_places_embedding_hnsw";
                ALTER TABLE places DROP COLUMN IF EXISTS embedding;
                """);
        }
    }
}
