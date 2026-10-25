using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UsageSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "usage_snapshots",
                schema: "billing",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metric = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false),
                    limit = table.Column<long>(type: "bigint", nullable: true),
                    taken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_snapshots", x => new { x.tenant_id, x.metric, x.day });
                });

            migrationBuilder.CreateIndex(
                name: "ix_usage_snapshots_tenant_id",
                schema: "billing",
                table: "usage_snapshots",
                column: "tenant_id");

            migrationBuilder.EnableTenantRls("billing", "usage_snapshots");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usage_snapshots",
                schema: "billing");
        }
    }
}
