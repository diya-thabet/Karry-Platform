using System;
using System.Collections.Generic;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Sync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CommandHandling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "processed_commands",
                schema: "sync",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    flags = table.Column<List<string>>(type: "text[]", nullable: false),
                    handled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_commands", x => new { x.device_id, x.command_id });
                    table.ForeignKey(
                        name: "fk_processed_commands_inbox_device_id_command_id",
                        columns: x => new { x.device_id, x.command_id },
                        principalSchema: "sync",
                        principalTable: "inbox",
                        principalColumns: new[] { "device_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "unhandled_commands",
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
                    reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unhandled_commands", x => new { x.device_id, x.id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_processed_commands_tenant_id",
                schema: "sync",
                table: "processed_commands",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_unhandled_commands_tenant_id",
                schema: "sync",
                table: "unhandled_commands",
                column: "tenant_id");

            // Hand-written (reviewed): tenant data under RLS, and append-only like the inbox (ADR-0031 §2, §5). The
            // trigger function is the inbox's own, from the Initial migration.
            migrationBuilder.EnableTenantRls("sync", "processed_commands");
            migrationBuilder.EnableTenantRls("sync", "unhandled_commands");
            migrationBuilder.Sql("""
                CREATE TRIGGER processed_commands_no_update_delete BEFORE UPDATE OR DELETE ON sync.processed_commands
                  FOR EACH ROW EXECUTE FUNCTION sync.reject_inbox_change();
                CREATE TRIGGER processed_commands_no_truncate BEFORE TRUNCATE ON sync.processed_commands
                  FOR EACH STATEMENT EXECUTE FUNCTION sync.reject_inbox_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON sync.processed_commands FROM karry_app;
                CREATE TRIGGER unhandled_commands_no_update_delete BEFORE UPDATE OR DELETE ON sync.unhandled_commands
                  FOR EACH ROW EXECUTE FUNCTION sync.reject_inbox_change();
                CREATE TRIGGER unhandled_commands_no_truncate BEFORE TRUNCATE ON sync.unhandled_commands
                  FOR EACH STATEMENT EXECUTE FUNCTION sync.reject_inbox_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON sync.unhandled_commands FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "processed_commands",
                schema: "sync");

            migrationBuilder.DropTable(
                name: "unhandled_commands",
                schema: "sync");
        }
    }
}
