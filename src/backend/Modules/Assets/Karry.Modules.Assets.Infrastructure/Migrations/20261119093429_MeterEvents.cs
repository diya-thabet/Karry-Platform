using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Assets.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MeterEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_meters_values",
                schema: "assets",
                table: "meters");

            migrationBuilder.CreateTable(
                name: "meter_events",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    meter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    value_before = table.Column<decimal>(type: "numeric(9,1)", precision: 9, scale: 1, nullable: true),
                    value_after = table.Column<decimal>(type: "numeric(9,1)", precision: 9, scale: 1, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    photos = table.Column<string[]>(type: "text[]", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meter_events", x => x.id);
                    table.CheckConstraint("ck_meter_events_values", "(kind IN ('Replaced', 'Reset') AND value_before IS NOT NULL AND value_after IS NOT NULL) OR (kind = 'Failed' AND value_after IS NULL) OR (kind = 'Repaired' AND value_before IS NULL AND value_after IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_meter_events_meters_meter_id",
                        column: x => x.meter_id,
                        principalSchema: "assets",
                        principalTable: "meters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_meters_values",
                schema: "assets",
                table: "meters",
                sql: "initial_reading >= 0 AND last_value >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_meter_events_meter_id",
                schema: "assets",
                table: "meter_events",
                column: "meter_id");

            migrationBuilder.CreateIndex(
                name: "ix_meter_events_tenant_id",
                schema: "assets",
                table: "meter_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_meter_events_tenant_id_meter_id_occurred_at",
                schema: "assets",
                table: "meter_events",
                columns: new[] { "tenant_id", "meter_id", "occurred_at" });

            // Hand-written (reviewed): meter events are tenant data and append-only (ADR-0036 §5), like the readings.
            migrationBuilder.EnableTenantRls("assets", "meter_events");
            migrationBuilder.Sql("""
                CREATE TRIGGER meter_events_no_update_delete BEFORE UPDATE OR DELETE ON assets.meter_events
                  FOR EACH ROW EXECUTE FUNCTION assets.reject_history_change();
                CREATE TRIGGER meter_events_no_truncate BEFORE TRUNCATE ON assets.meter_events
                  FOR EACH STATEMENT EXECUTE FUNCTION assets.reject_history_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON assets.meter_events FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meter_events",
                schema: "assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_meters_values",
                schema: "assets",
                table: "meters");

            migrationBuilder.AddCheckConstraint(
                name: "ck_meters_values",
                schema: "assets",
                table: "meters",
                sql: "initial_reading >= 0 AND last_value >= initial_reading");
        }
    }
}
