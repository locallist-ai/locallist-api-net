using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS refresh_tokens (
                    id uuid NOT NULL DEFAULT gen_random_uuid(),
                    user_id uuid NOT NULL,
                    token_hash character varying(255) NOT NULL,
                    token_prefix character varying(16) NOT NULL,
                    expires_at timestamp with time zone NOT NULL,
                    created_at timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_refresh_tokens" PRIMARY KEY (id),
                    CONSTRAINT "FK_refresh_tokens_users_user_id"
                        FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS "IX_refresh_tokens_TokenPrefix"
                    ON refresh_tokens (token_prefix);
                CREATE INDEX IF NOT EXISTS "IX_refresh_tokens_UserId"
                    ON refresh_tokens (user_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "refresh_tokens");
        }
    }
}
