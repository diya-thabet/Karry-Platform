using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Sync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DeviceClocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "clock_offset_ms",
                schema: "sync",
                table: "waiting_commands",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "device_clocks",
                schema: "sync",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offset_ms = table.Column<int>(type: "integer", nullable: false),
                    measured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_clocks", x => x.device_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_clocks_tenant_id",
                schema: "sync",
                table: "device_clocks",
                column: "tenant_id");

            // Hand-written (reviewed): tenant data under RLS. Not append-only: the last measured clock offset is updated.
            migrationBuilder.EnableTenantRls("sync", "device_clocks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_clocks",
                schema: "sync");

            migrationBuilder.DropColumn(
                name: "clock_offset_ms",
                schema: "sync",
                table: "waiting_commands");
        }
    }
}
