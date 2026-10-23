using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Trust.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Anchors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "anchors",
                schema: "audit",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    entry_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    anchored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    object_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_anchors", x => new { x.tenant_id, x.seq });
                });

            migrationBuilder.CreateIndex(
                name: "ix_anchors_tenant_id",
                schema: "audit",
                table: "anchors",
                column: "tenant_id");

            // Hand-written (reviewed): anchor bookkeeping is tenant data.
            migrationBuilder.EnableTenantRls("audit", "anchors");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "anchors",
                schema: "audit");
        }
    }
}
