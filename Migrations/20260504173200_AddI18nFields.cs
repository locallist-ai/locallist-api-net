using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddI18nFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE places ADD COLUMN IF NOT EXISTS name_i18n jsonb;
                ALTER TABLE places ADD COLUMN IF NOT EXISTS why_this_place_i18n jsonb;
                ALTER TABLE places ADD COLUMN IF NOT EXISTS best_time_i18n jsonb;
                ALTER TABLE places ADD COLUMN IF NOT EXISTS neighborhood_i18n jsonb;
                ALTER TABLE places ADD COLUMN IF NOT EXISTS subcategory_i18n jsonb;
                ALTER TABLE places ADD COLUMN IF NOT EXISTS best_for_i18n jsonb;
                ALTER TABLE places ADD COLUMN IF NOT EXISTS suitable_for_i18n jsonb;
                ALTER TABLE places ADD COLUMN IF NOT EXISTS translation_status jsonb;

                ALTER TABLE plans ADD COLUMN IF NOT EXISTS name_i18n jsonb;
                ALTER TABLE plans ADD COLUMN IF NOT EXISTS description_i18n jsonb;

                UPDATE places SET name_i18n = jsonb_build_object('en', name)
                    WHERE name_i18n IS NULL AND name IS NOT NULL;
                UPDATE places SET why_this_place_i18n = jsonb_build_object('en', why_this_place)
                    WHERE why_this_place_i18n IS NULL AND why_this_place IS NOT NULL;
                UPDATE places SET best_time_i18n = jsonb_build_object('en', best_time)
                    WHERE best_time_i18n IS NULL AND best_time IS NOT NULL;
                UPDATE places SET neighborhood_i18n = jsonb_build_object('en', neighborhood)
                    WHERE neighborhood_i18n IS NULL AND neighborhood IS NOT NULL;
                UPDATE places SET subcategory_i18n = jsonb_build_object('en', subcategory)
                    WHERE subcategory_i18n IS NULL AND subcategory IS NOT NULL;
                UPDATE places SET best_for_i18n = jsonb_build_object('en', to_json(best_for)::jsonb)
                    WHERE best_for_i18n IS NULL AND best_for IS NOT NULL;
                UPDATE places SET suitable_for_i18n = jsonb_build_object('en', to_json(suitable_for)::jsonb)
                    WHERE suitable_for_i18n IS NULL AND suitable_for IS NOT NULL;
                UPDATE plans SET name_i18n = jsonb_build_object('en', name)
                    WHERE name_i18n IS NULL AND name IS NOT NULL;
                UPDATE plans SET description_i18n = jsonb_build_object('en', description)
                    WHERE description_i18n IS NULL AND description IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE plans DROP COLUMN IF EXISTS description_i18n;
                ALTER TABLE plans DROP COLUMN IF EXISTS name_i18n;
                ALTER TABLE places DROP COLUMN IF EXISTS best_for_i18n;
                ALTER TABLE places DROP COLUMN IF EXISTS best_time_i18n;
                ALTER TABLE places DROP COLUMN IF EXISTS name_i18n;
                ALTER TABLE places DROP COLUMN IF EXISTS neighborhood_i18n;
                ALTER TABLE places DROP COLUMN IF EXISTS subcategory_i18n;
                ALTER TABLE places DROP COLUMN IF EXISTS suitable_for_i18n;
                ALTER TABLE places DROP COLUMN IF EXISTS translation_status;
                ALTER TABLE places DROP COLUMN IF EXISTS why_this_place_i18n;
                """);
        }
    }
}
