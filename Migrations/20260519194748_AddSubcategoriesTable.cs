using System;
using LocalList.API.NET.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    [DbContext(typeof(LocalListDbContext))]
    [Migration("20260519194748_AddSubcategoriesTable")]
    public partial class AddSubcategoriesTable : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "subcategories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    label_en = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    label_es = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_admin_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subcategories", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_subcategories_category_key_key",
                table: "subcategories",
                columns: new[] { "category_key", "key" },
                unique: true,
                filter: "deleted_at IS NULL");

            // Seed existing hardcoded subcategories
            var now = new DateTimeOffset(2026, 5, 19, 0, 0, 0, TimeSpan.Zero);
            var seed = new (string Cat, string Key, string LabelEn, string LabelEs)[]
            {
                // Food
                ("Food", "ramen", "Ramen", "Ramen"),
                ("Food", "sushi", "Sushi", "Sushi"),
                ("Food", "italian", "Italian", "Italiana"),
                ("Food", "pizza", "Pizza", "Pizza"),
                ("Food", "mexican", "Mexican", "Mexicana"),
                ("Food", "tacos", "Tacos", "Tacos"),
                ("Food", "cuban", "Cuban", "Cubana"),
                ("Food", "latin-american", "Latin American", "Latinoamericana"),
                ("Food", "american", "American", "Americana"),
                ("Food", "steakhouse", "Steakhouse", "Asador"),
                ("Food", "seafood", "Seafood", "Mariscos"),
                ("Food", "mediterranean", "Mediterranean", "Mediterránea"),
                ("Food", "asian-fusion", "Asian Fusion", "Fusión Asiática"),
                ("Food", "brunch", "Brunch", "Brunch"),
                ("Food", "bakery", "Bakery", "Panadería"),
                ("Food", "vegan", "Vegan", "Vegana"),
                // Nightlife
                ("Nightlife", "pub", "Pub", "Pub"),
                ("Nightlife", "cocktail-bar", "Cocktail Bar", "Coctelería"),
                ("Nightlife", "speakeasy", "Speakeasy", "Speakeasy"),
                ("Nightlife", "rooftop-bar", "Rooftop Bar", "Bar en Azotea"),
                ("Nightlife", "wine-bar", "Wine Bar", "Bar de Vinos"),
                ("Nightlife", "sports-bar", "Sports Bar", "Bar Deportivo"),
                ("Nightlife", "beer-bar", "Beer Bar", "Bar de Cervezas"),
                ("Nightlife", "nightclub", "Nightclub", "Discoteca"),
                ("Nightlife", "live-music", "Live Music", "Música en Directo"),
                // Coffee
                ("Coffee", "specialty-coffee", "Specialty Coffee", "Café de Especialidad"),
                ("Coffee", "espresso-bar", "Espresso Bar", "Cafetería Espresso"),
                ("Coffee", "bakery-cafe", "Bakery Café", "Café Panadería"),
                ("Coffee", "tea-house", "Tea House", "Salón de Té"),
                ("Coffee", "juice-bar", "Juice Bar", "Bar de Zumos"),
                ("Coffee", "dessert", "Dessert", "Postres"),
                // Outdoors
                ("Outdoors", "beach", "Beach", "Playa"),
                ("Outdoors", "park", "Park", "Parque"),
                ("Outdoors", "garden", "Garden", "Jardín"),
                ("Outdoors", "trail", "Trail", "Senda"),
                ("Outdoors", "marina", "Marina", "Marina"),
                ("Outdoors", "pier", "Pier", "Muelle"),
                ("Outdoors", "waterfront", "Waterfront", "Paseo Marítimo"),
                ("Outdoors", "dog-park", "Dog Park", "Parque Canino"),
                // Wellness
                ("Wellness", "spa", "Spa", "Spa"),
                ("Wellness", "pilates", "Pilates", "Pilates"),
                ("Wellness", "yoga", "Yoga", "Yoga"),
                ("Wellness", "gym", "Gym", "Gimnasio"),
                ("Wellness", "sauna", "Sauna", "Sauna"),
                ("Wellness", "iv-therapy", "IV Therapy", "Terapia IV"),
                ("Wellness", "massage", "Massage", "Masaje"),
                ("Wellness", "salt-cave", "Salt Cave", "Cueva de Sal"),
                // Culture
                ("Culture", "museum", "Museum", "Museo"),
                ("Culture", "gallery", "Gallery", "Galería"),
                ("Culture", "theater", "Theater", "Teatro"),
                ("Culture", "music-venue", "Music Venue", "Sala de Música"),
                ("Culture", "festival-site", "Festival Site", "Recinto de Festivales"),
                ("Culture", "historic-site", "Historic Site", "Lugar Histórico"),
                ("Culture", "public-art", "Public Art", "Arte Público"),
                ("Culture", "cultural-center", "Cultural Center", "Centro Cultural"),
                // Shopping
                ("Shopping", "boutique", "Boutique", "Boutique"),
                ("Shopping", "vintage", "Vintage", "Vintage"),
                ("Shopping", "bookstore", "Bookstore", "Librería"),
                ("Shopping", "record-store", "Record Store", "Tienda de Discos"),
                ("Shopping", "concept-store", "Concept Store", "Concept Store"),
                ("Shopping", "market", "Market", "Mercado"),
                ("Shopping", "florist", "Florist", "Floristería"),
                ("Shopping", "designer", "Designer", "Diseñador"),
            };

            var columnTypes = new[] { "uuid", "character varying(50)", "character varying(80)", "character varying(120)", "character varying(120)", "timestamp with time zone", "timestamp with time zone" };
            foreach (var (cat, subKey, labelEn, labelEs) in seed)
            {
                migrationBuilder.InsertData(
                    table: "subcategories",
                    columns: new[] { "id", "category_key", "key", "label_en", "label_es", "created_at", "updated_at" },
                    columnTypes: columnTypes,
                    values: new object[] { Guid.NewGuid(), cat, subKey, labelEn, labelEs, now, now });
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "subcategories");
        }
    }
}
