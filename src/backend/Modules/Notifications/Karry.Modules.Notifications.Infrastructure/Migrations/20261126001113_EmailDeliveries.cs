using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Notifications.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EmailDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_deliveries",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_deliveries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_email_deliveries_tenant_id",
                schema: "notifications",
                table: "email_deliveries",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_email_deliveries_tenant_id_created_at",
                schema: "notifications",
                table: "email_deliveries",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_email_deliveries_tenant_id_key",
                schema: "notifications",
                table: "email_deliveries",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_deliveries_tenant_id_status_next_attempt_at",
                schema: "notifications",
                table: "email_deliveries",
                columns: new[] { "tenant_id", "status", "next_attempt_at" });

            // Tenant isolation (ADR-0006): each company sees only its own e-mails.
            migrationBuilder.EnableTenantRls("notifications", "email_deliveries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_deliveries",
                schema: "notifications");
        }
    }
}
