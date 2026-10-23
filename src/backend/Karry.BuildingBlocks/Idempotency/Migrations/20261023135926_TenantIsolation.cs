using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.BuildingBlocks.Idempotency.Migrations
{
    /// <inheritdoc />
    public partial class TenantIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "platform",
                table: "outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "platform",
                table: "idempotency_records",
                type: "uuid",
                nullable: true);

            // Hand-written: stored responses contain tenant data. Tenant rows are visible only to their tenant;
            // rows of operator (no-tenant) requests only without a tenant.
            migrationBuilder.EnableTenantRls("platform", "idempotency_records", allowPlatformRows: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY tenant_isolation ON platform.idempotency_records; ALTER TABLE platform.idempotency_records NO FORCE ROW LEVEL SECURITY; ALTER TABLE platform.idempotency_records DISABLE ROW LEVEL SECURITY;");
            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "platform",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "platform",
                table: "idempotency_records");
        }
    }
}
