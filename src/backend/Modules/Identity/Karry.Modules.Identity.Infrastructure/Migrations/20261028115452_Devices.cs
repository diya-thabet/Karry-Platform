using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Devices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "pin_failures",
                schema: "identity",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pin_locked_until",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "device_assertions",
                schema: "identity",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assertion_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_assertions", x => new { x.device_id, x.assertion_id });
                });

            migrationBuilder.CreateTable(
                name: "device_enrollments",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_enrollments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    platform = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    app_version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    public_key_x = table.Column<byte[]>(type: "bytea", nullable: false),
                    public_key_y = table.Column<byte[]>(type: "bytea", nullable: false),
                    pin_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    enrolled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_assertions_expires_at",
                schema: "identity",
                table: "device_assertions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_device_enrollments_tenant_id",
                schema: "identity",
                table: "device_enrollments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_enrollments_tenant_id_code_hash",
                schema: "identity",
                table: "device_enrollments",
                columns: new[] { "tenant_id", "code_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_devices_tenant_id",
                schema: "identity",
                table: "devices",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_devices_tenant_id_site_id",
                schema: "identity",
                table: "devices",
                columns: new[] { "tenant_id", "site_id" });

            // Tablets, their codes and their used assertion IDs belong to one tenant each (ADR-0028).
            migrationBuilder.EnableTenantRls("identity", "devices");
            migrationBuilder.EnableTenantRls("identity", "device_enrollments");
            migrationBuilder.EnableTenantRls("identity", "device_assertions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_assertions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "device_enrollments",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "devices",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "pin_failures",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "pin_locked_until",
                schema: "identity",
                table: "users");
        }
    }
}
