using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.FieldOps.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChecklistSubmissionsAndHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "checklist_submissions",
                schema: "fieldops",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    root_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    template_version = table.Column<int>(type: "integer", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    answers = table.Column<string>(type: "jsonb", nullable: false),
                    critical_items = table.Column<string[]>(type: "text[]", nullable: false),
                    envelope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_checklist_submissions", x => new { x.tenant_id, x.root_id });
                    table.CheckConstraint("ck_checklist_submissions_version", "template_version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "safety_holds",
                schema: "fieldops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    items = table.Column<string[]>(type: "text[]", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    opened_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_safety_holds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "safety_hold_releases",
                schema: "fieldops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_safety_hold_releases", x => x.id);
                    table.CheckConstraint("ck_safety_hold_releases_reason", "char_length(reason) BETWEEN 10 AND 500");
                    table.ForeignKey(
                        name: "fk_safety_hold_releases_safety_holds_id",
                        column: x => x.id,
                        principalSchema: "fieldops",
                        principalTable: "safety_holds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_checklist_submissions_command_id",
                schema: "fieldops",
                table: "checklist_submissions",
                column: "command_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_checklist_submissions_tenant_id",
                schema: "fieldops",
                table: "checklist_submissions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_checklist_submissions_tenant_id_asset_id_submitted_at",
                schema: "fieldops",
                table: "checklist_submissions",
                columns: new[] { "tenant_id", "asset_id", "submitted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_safety_hold_releases_tenant_id",
                schema: "fieldops",
                table: "safety_hold_releases",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_safety_holds_tenant_id",
                schema: "fieldops",
                table: "safety_holds",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_safety_holds_tenant_id_asset_id_opened_at",
                schema: "fieldops",
                table: "safety_holds",
                columns: new[] { "tenant_id", "asset_id", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "ix_safety_holds_tenant_id_submission_id",
                schema: "fieldops",
                table: "safety_holds",
                columns: new[] { "tenant_id", "submission_id" },
                unique: true);

            migrationBuilder.EnableTenantRls("fieldops", "checklist_submissions");
            migrationBuilder.EnableTenantRls("fieldops", "safety_holds");
            migrationBuilder.EnableTenantRls("fieldops", "safety_hold_releases");
            // Signed checklists and hold decisions are facts: never changed, never removed (ADR-0035 §3-§5).
            migrationBuilder.Sql("""
                CREATE TRIGGER checklist_submissions_no_update_delete BEFORE UPDATE OR DELETE ON fieldops.checklist_submissions
                  FOR EACH ROW EXECUTE FUNCTION fieldops.reject_change();
                CREATE TRIGGER checklist_submissions_no_truncate BEFORE TRUNCATE ON fieldops.checklist_submissions
                  FOR EACH STATEMENT EXECUTE FUNCTION fieldops.reject_change();
                CREATE TRIGGER safety_holds_no_update_delete BEFORE UPDATE OR DELETE ON fieldops.safety_holds
                  FOR EACH ROW EXECUTE FUNCTION fieldops.reject_change();
                CREATE TRIGGER safety_holds_no_truncate BEFORE TRUNCATE ON fieldops.safety_holds
                  FOR EACH STATEMENT EXECUTE FUNCTION fieldops.reject_change();
                CREATE TRIGGER safety_hold_releases_no_update_delete BEFORE UPDATE OR DELETE ON fieldops.safety_hold_releases
                  FOR EACH ROW EXECUTE FUNCTION fieldops.reject_change();
                CREATE TRIGGER safety_hold_releases_no_truncate BEFORE TRUNCATE ON fieldops.safety_hold_releases
                  FOR EACH STATEMENT EXECUTE FUNCTION fieldops.reject_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON fieldops.checklist_submissions, fieldops.safety_holds, fieldops.safety_hold_releases FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "checklist_submissions",
                schema: "fieldops");

            migrationBuilder.DropTable(
                name: "safety_hold_releases",
                schema: "fieldops");

            migrationBuilder.DropTable(
                name: "safety_holds",
                schema: "fieldops");
        }
    }
}
