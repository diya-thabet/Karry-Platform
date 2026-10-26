using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Tenancy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TenantSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "currency",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "EUR");

            migrationBuilder.AddColumn<int>(
                name: "fiscal_year_start_month",
                schema: "tenancy",
                table: "tenants",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "language",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "fr");

            migrationBuilder.AddColumn<string>(
                name: "legal_name",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "logo_sha256",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_colour",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "settings_changed_at",
                schema: "tenancy",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "time_zone",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "UTC");

            migrationBuilder.AddColumn<string>(
                name: "unit_system",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Metric");

            migrationBuilder.CreateTable(
                name: "tenant_logos",
                schema: "tenancy",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_logos", x => x.tenant_id);
                    table.CheckConstraint("ck_tenant_logos_size", "octet_length(content) <= 262144");
                });

            // Existing companies: the legal name starts as the display name; the other settings take the defaults above.
            migrationBuilder.Sql("UPDATE tenancy.tenants SET legal_name = name;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_logos ADD CONSTRAINT fk_tenant_logos_tenants FOREIGN KEY (tenant_id) REFERENCES tenancy.tenants (id) ON DELETE CASCADE;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenants_fiscal_year_start_month",
                schema: "tenancy",
                table: "tenants",
                sql: "fiscal_year_start_month BETWEEN 1 AND 12");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_logos",
                schema: "tenancy");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenants_fiscal_year_start_month",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "currency",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "fiscal_year_start_month",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "language",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "legal_name",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "logo_sha256",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "primary_colour",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "settings_changed_at",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "time_zone",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "unit_system",
                schema: "tenancy",
                table: "tenants");
        }
    }
}
