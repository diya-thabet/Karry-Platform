using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Trust.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AuditExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "exports",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    exported_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    filters = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    rows = table.Column<long>(type: "bigint", nullable: false),
                    up_to_sequence = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exports", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_exports_tenant_id",
                schema: "audit",
                table: "exports",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_exports_tenant_id_exported_at",
                schema: "audit",
                table: "exports",
                columns: new[] { "tenant_id", "exported_at" });

            // Hand-written (reviewed): tenant data. The evidence of an export is its ledger entry (ADR-0029 §2).
            migrationBuilder.EnableTenantRls("audit", "exports");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exports",
                schema: "audit");
        }
    }
}
