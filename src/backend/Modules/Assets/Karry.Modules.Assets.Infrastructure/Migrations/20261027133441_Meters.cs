using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Assets.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Meters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "meters",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    initial_reading = table.Column<decimal>(type: "numeric(9,1)", precision: 9, scale: 1, nullable: false),
                    installed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    offset = table.Column<decimal>(type: "numeric(9,1)", precision: 9, scale: 1, nullable: false),
                    last_value = table.Column<decimal>(type: "numeric(9,1)", precision: 9, scale: 1, nullable: false),
                    last_read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meters", x => x.id);
                    table.CheckConstraint("ck_meters_values", "initial_reading >= 0 AND last_value >= initial_reading");
                    table.ForeignKey(
                        name: "fk_meters_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "assets",
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "meter_readings",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    meter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<decimal>(type: "numeric(9,1)", precision: 9, scale: 1, nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meter_readings", x => x.id);
                    table.ForeignKey(
                        name: "fk_meter_readings_meters_meter_id",
                        column: x => x.meter_id,
                        principalSchema: "assets",
                        principalTable: "meters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_meter_readings_meter_id_read_at_id",
                schema: "assets",
                table: "meter_readings",
                columns: new[] { "meter_id", "read_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_meter_readings_tenant_id",
                schema: "assets",
                table: "meter_readings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_meters_asset_id",
                schema: "assets",
                table: "meters",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_meters_tenant_id",
                schema: "assets",
                table: "meters",
                column: "tenant_id");

            // Hand-written (reviewed): meters are tenant data; readings are append-only (ADR-0026, TC-P1-AST-002).
            migrationBuilder.EnableTenantRls("assets", "meters");
            migrationBuilder.EnableTenantRls("assets", "meter_readings");
            migrationBuilder.Sql("""
                CREATE TRIGGER meter_readings_no_update_delete BEFORE UPDATE OR DELETE ON assets.meter_readings
                  FOR EACH ROW EXECUTE FUNCTION assets.reject_history_change();
                CREATE TRIGGER meter_readings_no_truncate BEFORE TRUNCATE ON assets.meter_readings
                  FOR EACH STATEMENT EXECUTE FUNCTION assets.reject_history_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON assets.meter_readings FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meter_readings",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "meters",
                schema: "assets");
        }
    }
}
