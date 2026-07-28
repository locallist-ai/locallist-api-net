using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingEventAnalyticsColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cancel_reason",
                table: "billing_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "country_code",
                table: "billing_events",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "currency",
                table: "billing_events",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_trial_conversion",
                table: "billing_events",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "period_type",
                table: "billing_events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "price",
                table: "billing_events",
                type: "numeric(12,4)",
                precision: 12,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "price_in_purchased_currency",
                table: "billing_events",
                type: "numeric(12,4)",
                precision: 12,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "product_id",
                table: "billing_events",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "store",
                table: "billing_events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_billing_events_event_timestamp_ms",
                table: "billing_events",
                column: "event_timestamp_ms");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_billing_events_event_timestamp_ms",
                table: "billing_events");

            migrationBuilder.DropColumn(
                name: "cancel_reason",
                table: "billing_events");

            migrationBuilder.DropColumn(
                name: "country_code",
                table: "billing_events");

            migrationBuilder.DropColumn(
                name: "currency",
                table: "billing_events");

            migrationBuilder.DropColumn(
                name: "is_trial_conversion",
                table: "billing_events");

            migrationBuilder.DropColumn(
                name: "period_type",
                table: "billing_events");

            migrationBuilder.DropColumn(
                name: "price",
                table: "billing_events");

            migrationBuilder.DropColumn(
                name: "price_in_purchased_currency",
                table: "billing_events");

            migrationBuilder.DropColumn(
                name: "product_id",
                table: "billing_events");

            migrationBuilder.DropColumn(
                name: "store",
                table: "billing_events");
        }
    }
}
