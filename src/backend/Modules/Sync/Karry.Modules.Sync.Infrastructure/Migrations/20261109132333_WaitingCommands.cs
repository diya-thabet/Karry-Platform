using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Sync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WaitingCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "waiting_commands",
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
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_waiting_commands", x => new { x.device_id, x.id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_waiting_commands_device_id_sequence",
                schema: "sync",
                table: "waiting_commands",
                columns: new[] { "device_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_waiting_commands_tenant_id",
                schema: "sync",
                table: "waiting_commands",
                column: "tenant_id");

            // Hand-written (reviewed): tenant data under RLS, append-only like the inbox (ADR-0031 §4).
            migrationBuilder.EnableTenantRls("sync", "waiting_commands");
            migrationBuilder.Sql("""
                CREATE TRIGGER waiting_commands_no_update_delete BEFORE UPDATE OR DELETE ON sync.waiting_commands
                  FOR EACH ROW EXECUTE FUNCTION sync.reject_inbox_change();
                CREATE TRIGGER waiting_commands_no_truncate BEFORE TRUNCATE ON sync.waiting_commands
                  FOR EACH STATEMENT EXECUTE FUNCTION sync.reject_inbox_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON sync.waiting_commands FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "waiting_commands",
                schema: "sync");
        }
    }
}
