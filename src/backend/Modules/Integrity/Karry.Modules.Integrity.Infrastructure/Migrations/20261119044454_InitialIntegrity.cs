using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Integrity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "integrity");

            migrationBuilder.CreateTable(
                name: "flags",
                schema: "integrity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    severity = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    subject_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    related = table.Column<string>(type: "jsonb", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    closed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_flags", x => x.id);
                    table.CheckConstraint("ck_flags_closed", "(closed_at IS NULL) = (close_reason IS NULL)");
                    table.CheckConstraint("ck_flags_severity", "severity IN ('low', 'medium', 'high', 'critical')");
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "integrity",
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
                schema: "integrity",
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
                name: "ix_flags_tenant_id",
                schema: "integrity",
                table: "flags",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_flags_tenant_id_site_id_opened_at",
                schema: "integrity",
                table: "flags",
                columns: new[] { "tenant_id", "site_id", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "ix_flags_tenant_id_subject_type_subject_id_rule_key",
                schema: "integrity",
                table: "flags",
                columns: new[] { "tenant_id", "subject_type", "subject_id", "rule", "key" },
                unique: true,
                filter: "closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_aggregate_key_occurred_at",
                schema: "integrity",
                table: "outbox_messages",
                columns: new[] { "aggregate_key", "occurred_at" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_occurred_at_id",
                schema: "integrity",
                table: "outbox_messages",
                columns: new[] { "occurred_at", "id" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.EnableTenantRls("integrity", "flags");
            // A flag changes once, from open to closed (ADR-0036 §2); it is never deleted, nothing else of it changes.
            migrationBuilder.Sql("""
                CREATE FUNCTION integrity.only_close() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF TG_OP = 'UPDATE' AND OLD.closed_at IS NULL AND NEW.closed_at IS NOT NULL
                     AND (to_jsonb(NEW) - 'closed_at' - 'close_reason' - 'closed_by') = (to_jsonb(OLD) - 'closed_at' - 'close_reason' - 'closed_by') THEN
                    RETURN NEW;
                  END IF;
                  RAISE EXCEPTION 'integrity.flags: a flag only closes (% refused)', TG_OP USING ERRCODE = '42501';
                END
                $$;
                CREATE TRIGGER flags_only_close BEFORE UPDATE OR DELETE ON integrity.flags
                  FOR EACH ROW EXECUTE FUNCTION integrity.only_close();
                CREATE TRIGGER flags_no_truncate BEFORE TRUNCATE ON integrity.flags
                  FOR EACH STATEMENT EXECUTE FUNCTION integrity.only_close();
                REVOKE DELETE, TRUNCATE ON integrity.flags FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flags",
                schema: "integrity");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "integrity");

            migrationBuilder.DropTable(
                name: "processed_messages",
                schema: "integrity");

            migrationBuilder.Sql("DROP FUNCTION integrity.only_close();");
        }
    }
}
