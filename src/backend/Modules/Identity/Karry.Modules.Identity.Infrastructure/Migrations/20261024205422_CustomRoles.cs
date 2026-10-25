using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CustomRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "default_role_settings",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    field_levels = table.Column<string>(type: "jsonb", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_default_role_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    based_on = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    office = table.Column<bool>(type: "boolean", nullable: false),
                    permissions = table.Column<string>(type: "jsonb", nullable: false),
                    field_levels = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_default_role_settings_tenant_id",
                schema: "identity",
                table: "default_role_settings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_default_role_settings_tenant_id_role",
                schema: "identity",
                table: "default_role_settings",
                columns: new[] { "tenant_id", "role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_roles_tenant_id",
                schema: "identity",
                table: "roles",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_roles_tenant_id_code",
                schema: "identity",
                table: "roles",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_roles_tenant_id_name",
                schema: "identity",
                table: "roles",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            // Hand-written (reviewed): roles and field levels are tenant data.
            migrationBuilder.EnableTenantRls("identity", "roles");
            migrationBuilder.EnableTenantRls("identity", "default_role_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "default_role_settings",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "identity");
        }
    }
}
