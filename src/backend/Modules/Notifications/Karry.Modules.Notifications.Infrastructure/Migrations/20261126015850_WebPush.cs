using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Notifications.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WebPush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "push_pings",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_push_pings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "push_subscriptions",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    endpoint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_push_subscriptions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_push_pings_tenant_id",
                schema: "notifications",
                table: "push_pings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_push_pings_tenant_id_created_at",
                schema: "notifications",
                table: "push_pings",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_push_pings_tenant_id_status_next_attempt_at",
                schema: "notifications",
                table: "push_pings",
                columns: new[] { "tenant_id", "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_push_pings_tenant_id_subscription_id_status",
                schema: "notifications",
                table: "push_pings",
                columns: new[] { "tenant_id", "subscription_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_push_subscriptions_tenant_id",
                schema: "notifications",
                table: "push_subscriptions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_push_subscriptions_tenant_id_endpoint",
                schema: "notifications",
                table: "push_subscriptions",
                columns: new[] { "tenant_id", "endpoint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_push_subscriptions_tenant_id_owner_owner_id",
                schema: "notifications",
                table: "push_subscriptions",
                columns: new[] { "tenant_id", "owner", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_push_subscriptions_tenant_id_site_id",
                schema: "notifications",
                table: "push_subscriptions",
                columns: new[] { "tenant_id", "site_id" });

            // Tenant isolation (ADR-0006): each company sees only its own subscriptions and pushes.
            migrationBuilder.EnableTenantRls("notifications", "push_subscriptions");
            migrationBuilder.EnableTenantRls("notifications", "push_pings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "push_pings",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "push_subscriptions",
                schema: "notifications");
        }
    }
}
