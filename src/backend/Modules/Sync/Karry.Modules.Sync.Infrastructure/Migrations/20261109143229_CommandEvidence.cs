using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Sync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CommandEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "command_evidence",
                schema: "sync",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    content_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_command_evidence", x => new { x.device_id, x.command_id, x.sha256 });
                    table.ForeignKey(
                        name: "fk_command_evidence_inbox_device_id_command_id",
                        columns: x => new { x.device_id, x.command_id },
                        principalSchema: "sync",
                        principalTable: "inbox",
                        principalColumns: new[] { "device_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_command_evidence_device_id_sha256",
                schema: "sync",
                table: "command_evidence",
                columns: new[] { "device_id", "sha256" });

            migrationBuilder.CreateIndex(
                name: "ix_command_evidence_tenant_id",
                schema: "sync",
                table: "command_evidence",
                column: "tenant_id");

            // Hand-written (reviewed): tenant data under RLS, append-only like the inbox (ADR-0032: which command uses
            // which evidence is kept with the command).
            migrationBuilder.EnableTenantRls("sync", "command_evidence");
            migrationBuilder.Sql("""
                CREATE TRIGGER command_evidence_no_update_delete BEFORE UPDATE OR DELETE ON sync.command_evidence
                  FOR EACH ROW EXECUTE FUNCTION sync.reject_inbox_change();
                CREATE TRIGGER command_evidence_no_truncate BEFORE TRUNCATE ON sync.command_evidence
                  FOR EACH STATEMENT EXECUTE FUNCTION sync.reject_inbox_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON sync.command_evidence FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "command_evidence",
                schema: "sync");
        }
    }
}
