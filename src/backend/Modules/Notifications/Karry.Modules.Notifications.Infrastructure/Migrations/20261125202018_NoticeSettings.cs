using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Notifications.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NoticeSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "preferences",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_preferences", x => new { x.tenant_id, x.user_id, x.type, x.channel });
                });

            migrationBuilder.CreateTable(
                name: "rule_settings",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_preferences_tenant_id",
                schema: "notifications",
                table: "preferences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_rule_settings_tenant_id",
                schema: "notifications",
                table: "rule_settings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_rule_settings_tenant_id_type",
                schema: "notifications",
                table: "rule_settings",
                columns: new[] { "tenant_id", "type" },
                unique: true);

            // Tenant isolation (ADR-0006): each company sees only its own switches and its people's choices.
            migrationBuilder.EnableTenantRls("notifications", "rule_settings");
            migrationBuilder.EnableTenantRls("notifications", "preferences");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "preferences",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "rule_settings",
                schema: "notifications");
        }
    }
}
