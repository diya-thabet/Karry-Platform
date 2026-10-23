using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Trust.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                schema: "audit",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    payload_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    prev_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    entry_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_entries", x => new { x.tenant_id, x.seq });
                });

            migrationBuilder.CreateTable(
                name: "ledger_heads",
                schema: "audit",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_seq = table.Column<long>(type: "bigint", nullable: false),
                    last_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_heads", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "audit",
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
                schema: "audit",
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
                name: "ix_ledger_entries_event_id",
                schema: "audit",
                table: "ledger_entries",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_tenant_id",
                schema: "audit",
                table: "ledger_entries",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_heads_tenant_id",
                schema: "audit",
                table: "ledger_heads",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_aggregate_key_occurred_at",
                schema: "audit",
                table: "outbox_messages",
                columns: new[] { "aggregate_key", "occurred_at" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_occurred_at_id",
                schema: "audit",
                table: "outbox_messages",
                columns: new[] { "occurred_at", "id" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            // Hand-written (reviewed): the ledger is tenant data and append-only (ADR-0008, US-P1-AUD-01 AC2).
            migrationBuilder.EnableTenantRls("audit", "ledger_entries");
            migrationBuilder.EnableTenantRls("audit", "ledger_heads");
            migrationBuilder.Sql("""
                CREATE FUNCTION audit.reject_ledger_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  RAISE EXCEPTION 'audit.ledger_entries is append-only (% refused)', TG_OP USING ERRCODE = '42501';
                END
                $$;
                CREATE TRIGGER ledger_entries_no_update_delete BEFORE UPDATE OR DELETE ON audit.ledger_entries
                  FOR EACH ROW EXECUTE FUNCTION audit.reject_ledger_change();
                CREATE TRIGGER ledger_entries_no_truncate BEFORE TRUNCATE ON audit.ledger_entries
                  FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_ledger_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON audit.ledger_entries FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION audit.reject_ledger_change() CASCADE;");
            migrationBuilder.DropTable(
                name: "ledger_entries",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "ledger_heads",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "processed_messages",
                schema: "audit");
        }
    }
}
