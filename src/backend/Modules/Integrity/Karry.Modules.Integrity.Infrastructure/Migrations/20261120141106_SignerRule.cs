using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Integrity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SignerRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_rule_settings_security_on",
                schema: "integrity",
                table: "rule_settings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_rule_settings_security_on",
                schema: "integrity",
                table: "rule_settings",
                sql: "enabled OR rule NOT IN ('RULE-FO-05', 'RULE-FO-06', 'RULE-FO-07', 'RULE-FO-09')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_rule_settings_security_on",
                schema: "integrity",
                table: "rule_settings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_rule_settings_security_on",
                schema: "integrity",
                table: "rule_settings",
                sql: "enabled OR rule NOT IN ('RULE-FO-05', 'RULE-FO-06', 'RULE-FO-07')");
        }
    }
}
