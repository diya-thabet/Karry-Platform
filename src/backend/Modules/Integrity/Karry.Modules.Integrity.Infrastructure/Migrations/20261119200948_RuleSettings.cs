using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Integrity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RuleSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rule_settings",
                schema: "integrity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    thresholds = table.Column<string>(type: "jsonb", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_settings", x => x.id);
                    table.CheckConstraint("ck_rule_settings_security_on", "enabled OR rule NOT IN ('RULE-FO-05', 'RULE-FO-06', 'RULE-FO-07')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_rule_settings_tenant_id",
                schema: "integrity",
                table: "rule_settings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_rule_settings_tenant_id_rule",
                schema: "integrity",
                table: "rule_settings",
                columns: new[] { "tenant_id", "rule" },
                unique: true);

            migrationBuilder.EnableTenantRls("integrity", "rule_settings");
            // A setting is changed (ledgered), never removed: going back to the defaults is a change too.
            migrationBuilder.Sql("REVOKE DELETE, TRUNCATE ON integrity.rule_settings FROM karry_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rule_settings",
                schema: "integrity");
        }
    }
}
