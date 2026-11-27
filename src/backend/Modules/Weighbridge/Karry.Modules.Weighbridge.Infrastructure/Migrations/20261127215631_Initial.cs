using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Weighbridge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "weighbridge");

            migrationBuilder.CreateTable(
                name: "haulers",
                schema: "weighbridge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_haulers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "weighbridge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    aggregate_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dead_lettered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "processed_messages",
                schema: "weighbridge",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handler = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_messages", x => new { x.message_id, x.handler });
                });

            migrationBuilder.CreateTable(
                name: "trucks",
                schema: "weighbridge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plate = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    plate_key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    hauler_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tare_kg = table.Column<decimal>(type: "numeric(9,0)", precision: 9, scale: 0, nullable: true),
                    tare_on = table.Column<DateOnly>(type: "date", nullable: true),
                    tare_method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    max_payload_kg = table.Column<decimal>(type: "numeric(9,0)", precision: 9, scale: 0, nullable: true),
                    axles = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trucks", x => x.id);
                    table.CheckConstraint("ck_trucks_masses", "(tare_kg IS NULL OR tare_kg > 0) AND (max_payload_kg IS NULL OR max_payload_kg > 0)");
                    table.CheckConstraint("ck_trucks_tare", "(tare_kg IS NULL) = (tare_on IS NULL) AND (tare_kg IS NULL) = (tare_method IS NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_haulers_tenant_id",
                schema: "weighbridge",
                table: "haulers",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_haulers_tenant_id_name_key",
                schema: "weighbridge",
                table: "haulers",
                columns: new[] { "tenant_id", "name_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_aggregate_key_occurred_at",
                schema: "weighbridge",
                table: "outbox_messages",
                columns: new[] { "aggregate_key", "occurred_at" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_occurred_at_id",
                schema: "weighbridge",
                table: "outbox_messages",
                columns: new[] { "occurred_at", "id" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_trucks_tenant_id",
                schema: "weighbridge",
                table: "trucks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_trucks_tenant_id_plate_key",
                schema: "weighbridge",
                table: "trucks",
                columns: new[] { "tenant_id", "plate_key" },
                unique: true);

            migrationBuilder.EnableTenantRls("weighbridge", "haulers");
            migrationBuilder.EnableTenantRls("weighbridge", "trucks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "haulers",
                schema: "weighbridge");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "weighbridge");

            migrationBuilder.DropTable(
                name: "processed_messages",
                schema: "weighbridge");

            migrationBuilder.DropTable(
                name: "trucks",
                schema: "weighbridge");
        }
    }
}
