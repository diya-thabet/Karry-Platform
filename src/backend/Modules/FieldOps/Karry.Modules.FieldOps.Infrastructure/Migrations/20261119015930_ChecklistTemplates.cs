using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.FieldOps.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChecklistTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "checklist_templates",
                schema: "fieldops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    items = table.Column<string>(type: "jsonb", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_checklist_templates", x => x.id);
                    table.CheckConstraint("ck_checklist_templates_version", "version >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_checklist_templates_tenant_id",
                schema: "fieldops",
                table: "checklist_templates",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_checklist_templates_tenant_id_asset_type_version",
                schema: "fieldops",
                table: "checklist_templates",
                columns: new[] { "tenant_id", "asset_type", "version" },
                unique: true);

            // Tenant isolation, and a published version is never changed (ADR-0035 §2): the next change is the next version.
            migrationBuilder.EnableTenantRls("fieldops", "checklist_templates");
            migrationBuilder.Sql("""
                CREATE TRIGGER checklist_templates_no_update_delete BEFORE UPDATE OR DELETE ON fieldops.checklist_templates
                  FOR EACH ROW EXECUTE FUNCTION fieldops.reject_change();
                CREATE TRIGGER checklist_templates_no_truncate BEFORE TRUNCATE ON fieldops.checklist_templates
                  FOR EACH STATEMENT EXECUTE FUNCTION fieldops.reject_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON fieldops.checklist_templates FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "checklist_templates",
                schema: "fieldops");
        }
    }
}
