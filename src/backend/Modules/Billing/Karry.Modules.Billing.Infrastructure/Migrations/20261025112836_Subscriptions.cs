using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Subscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    terms = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    status_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trial_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    unpaid_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    issued_on = table.Column<DateOnly>(type: "date", nullable: false),
                    due_on = table.Column<DateOnly>(type: "date", nullable: false),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoices", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoices_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "billing",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_subscription_id",
                schema: "billing",
                table: "invoices",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id",
                schema: "billing",
                table: "invoices",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_number",
                schema: "billing",
                table: "invoices",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_tenant_id",
                schema: "billing",
                table: "subscriptions",
                column: "tenant_id",
                unique: true);

            // Tenant-owned: row-level security like every tenant table (US-P1-TEN-02 AC5).
            migrationBuilder.EnableTenantRls("billing", "subscriptions");
            migrationBuilder.EnableTenantRls("billing", "invoices");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoices",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "billing");
        }
    }
}
