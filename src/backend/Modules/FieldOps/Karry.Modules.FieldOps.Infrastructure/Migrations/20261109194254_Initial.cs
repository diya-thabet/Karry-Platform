using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.FieldOps.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "fieldops");

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "fieldops",
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
                schema: "fieldops",
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
                name: "shift_entries",
                schema: "fieldops",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    root_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    meter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    meter_kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment_no = table.Column<int>(type: "integer", nullable: false),
                    previous_root_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shift_date = table.Column<DateOnly>(type: "date", nullable: true),
                    shift_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    shift_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    shift_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    start_value = table.Column<decimal>(type: "numeric(9,1)", precision: 9, scale: 1, nullable: false),
                    fuel_start_percent = table.Column<int>(type: "integer", nullable: false),
                    start_photo = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    end_value = table.Column<decimal>(type: "numeric(9,1)", precision: 9, scale: 1, nullable: true),
                    fuel_end_percent = table.Column<int>(type: "integer", nullable: true),
                    end_photo = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: true),
                    stoppages = table.Column<string>(type: "jsonb", nullable: false),
                    remarks = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    estimated = table.Column<bool>(type: "boolean", nullable: false),
                    flags = table.Column<string[]>(type: "text[]", nullable: false),
                    envelope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shift_entries", x => new { x.tenant_id, x.root_id, x.version });
                    table.CheckConstraint("ck_shift_entries_closed", "(status = 'Closed') = (ended_at IS NOT NULL AND end_value IS NOT NULL AND end_photo IS NOT NULL)");
                    table.CheckConstraint("ck_shift_entries_fuel", "fuel_start_percent BETWEEN 0 AND 100 AND (fuel_end_percent IS NULL OR fuel_end_percent BETWEEN 0 AND 100)");
                    table.CheckConstraint("ck_shift_entries_kind", "meter_kind IN ('hours', 'km')");
                    table.CheckConstraint("ck_shift_entries_values", "start_value >= 0 AND (end_value IS NULL OR end_value >= 0)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_aggregate_key_occurred_at",
                schema: "fieldops",
                table: "outbox_messages",
                columns: new[] { "aggregate_key", "occurred_at" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_occurred_at_id",
                schema: "fieldops",
                table: "outbox_messages",
                columns: new[] { "occurred_at", "id" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_shift_entries_command_id",
                schema: "fieldops",
                table: "shift_entries",
                column: "command_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shift_entries_tenant_id",
                schema: "fieldops",
                table: "shift_entries",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_shift_entries_tenant_id_meter_id_started_at",
                schema: "fieldops",
                table: "shift_entries",
                columns: new[] { "tenant_id", "meter_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_shift_entries_tenant_id_operator_id_started_at",
                schema: "fieldops",
                table: "shift_entries",
                columns: new[] { "tenant_id", "operator_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_shift_entries_tenant_id_site_id_shift_date",
                schema: "fieldops",
                table: "shift_entries",
                columns: new[] { "tenant_id", "site_id", "shift_date" });

            // Tenant isolation, and shift entries are append-only (ADR-0034 §3): a change is a new version, never an edit.
            migrationBuilder.EnableTenantRls("fieldops", "shift_entries");
            migrationBuilder.Sql("""
                CREATE FUNCTION fieldops.reject_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  RAISE EXCEPTION 'fieldops.% is append-only (% refused)', TG_TABLE_NAME, TG_OP USING ERRCODE = '42501';
                END
                $$;
                CREATE TRIGGER shift_entries_no_update_delete BEFORE UPDATE OR DELETE ON fieldops.shift_entries
                  FOR EACH ROW EXECUTE FUNCTION fieldops.reject_change();
                CREATE TRIGGER shift_entries_no_truncate BEFORE TRUNCATE ON fieldops.shift_entries
                  FOR EACH STATEMENT EXECUTE FUNCTION fieldops.reject_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON fieldops.shift_entries FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "fieldops");

            migrationBuilder.DropTable(
                name: "processed_messages",
                schema: "fieldops");

            migrationBuilder.DropTable(
                name: "shift_entries",
                schema: "fieldops");

            migrationBuilder.Sql("DROP FUNCTION fieldops.reject_change();");
        }
    }
}
