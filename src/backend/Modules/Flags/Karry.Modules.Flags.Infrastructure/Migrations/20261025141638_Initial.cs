using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Flags.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "flags");

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "flags",
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
                name: "platform_flag_changes",
                schema: "flags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    before = table.Column<bool>(type: "boolean", nullable: true),
                    after = table.Column<bool>(type: "boolean", nullable: true),
                    actor = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_flag_changes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_flags",
                schema: "flags",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<bool>(type: "boolean", nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_flags", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "processed_messages",
                schema: "flags",
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
                name: "tenant_flags",
                schema: "flags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    value = table.Column<bool>(type: "boolean", nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_flags", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_aggregate_key_occurred_at",
                schema: "flags",
                table: "outbox_messages",
                columns: new[] { "aggregate_key", "occurred_at" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_occurred_at_id",
                schema: "flags",
                table: "outbox_messages",
                columns: new[] { "occurred_at", "id" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_platform_flag_changes_key_at",
                schema: "flags",
                table: "platform_flag_changes",
                columns: new[] { "key", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_flags_tenant_id",
                schema: "flags",
                table: "tenant_flags",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_flags_tenant_id_key_subject",
                schema: "flags",
                table: "tenant_flags",
                columns: new[] { "tenant_id", "key", "subject" },
                unique: true);

            // Hand-written (reviewed): company overrides are tenant data; platform history is append-only (ADR-0019).
            migrationBuilder.EnableTenantRls("flags", "tenant_flags");
            migrationBuilder.Sql("""
                CREATE FUNCTION flags.reject_history_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  RAISE EXCEPTION 'flags.platform_flag_changes is append-only (% refused)', TG_OP USING ERRCODE = '42501';
                END
                $$;
                CREATE TRIGGER platform_flag_changes_no_update_delete BEFORE UPDATE OR DELETE ON flags.platform_flag_changes
                  FOR EACH ROW EXECUTE FUNCTION flags.reject_history_change();
                CREATE TRIGGER platform_flag_changes_no_truncate BEFORE TRUNCATE ON flags.platform_flag_changes
                  FOR EACH STATEMENT EXECUTE FUNCTION flags.reject_history_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON flags.platform_flag_changes FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION flags.reject_history_change() CASCADE;");
            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "flags");

            migrationBuilder.DropTable(
                name: "platform_flag_changes",
                schema: "flags");

            migrationBuilder.DropTable(
                name: "platform_flags",
                schema: "flags");

            migrationBuilder.DropTable(
                name: "processed_messages",
                schema: "flags");

            migrationBuilder.DropTable(
                name: "tenant_flags",
                schema: "flags");
        }
    }
}
