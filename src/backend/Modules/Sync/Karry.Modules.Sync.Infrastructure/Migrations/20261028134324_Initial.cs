using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Sync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sync");

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "sync",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    device_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    signature = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox", x => new { x.device_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "sync",
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
                schema: "sync",
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

            migrationBuilder.CreateIndex(
                name: "ix_inbox_device_id_sequence",
                schema: "sync",
                table: "inbox",
                columns: new[] { "device_id", "sequence" },
                unique: true,
                filter: "status = 'Received'");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_tenant_id",
                schema: "sync",
                table: "inbox",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_aggregate_key_occurred_at",
                schema: "sync",
                table: "outbox_messages",
                columns: new[] { "aggregate_key", "occurred_at" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_occurred_at_id",
                schema: "sync",
                table: "outbox_messages",
                columns: new[] { "occurred_at", "id" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            // Hand-written (reviewed): the inbox is tenant data, and a signed command never changes (ADR-0028 §6).
            migrationBuilder.EnableTenantRls("sync", "inbox");
            migrationBuilder.Sql("""
                CREATE FUNCTION sync.reject_inbox_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  RAISE EXCEPTION '%.% is append-only (% refused)', TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP USING ERRCODE = '42501';
                END
                $$;
                CREATE TRIGGER inbox_no_update_delete BEFORE UPDATE OR DELETE ON sync.inbox
                  FOR EACH ROW EXECUTE FUNCTION sync.reject_inbox_change();
                CREATE TRIGGER inbox_no_truncate BEFORE TRUNCATE ON sync.inbox
                  FOR EACH STATEMENT EXECUTE FUNCTION sync.reject_inbox_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON sync.inbox FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION sync.reject_inbox_change() CASCADE;");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "sync");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "sync");

            migrationBuilder.DropTable(
                name: "processed_messages",
                schema: "sync");
        }
    }
}
