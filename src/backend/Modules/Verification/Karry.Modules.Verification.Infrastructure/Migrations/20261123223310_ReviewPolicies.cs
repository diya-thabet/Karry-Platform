using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Verification.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReviewPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_review_actions_kind",
                schema: "verification",
                table: "review_actions");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "due_at",
                schema: "verification",
                table: "review_items",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<int>(
                name: "policy_version",
                schema: "verification",
                table: "review_items",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "review_policies",
                schema: "verification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    auto_approve = table.Column<bool>(type: "boolean", nullable: false),
                    four_eyes_hours = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: false),
                    four_eyes_share = table.Column<decimal>(type: "numeric(3,2)", precision: 3, scale: 2, nullable: false),
                    sla_hours = table.Column<int>(type: "integer", nullable: false),
                    escalate_to = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_policies", x => x.id);
                    table.CheckConstraint("ck_review_policies_terms", "four_eyes_hours BETWEEN 0 AND 24 AND four_eyes_share BETWEEN 0 AND 1 AND sla_hours BETWEEN 1 AND 168");
                    table.CheckConstraint("ck_review_policies_version", "version >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_review_items_tenant_id_status_due_at",
                schema: "verification",
                table: "review_items",
                columns: new[] { "tenant_id", "status", "due_at" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_review_actions_kind",
                schema: "verification",
                table: "review_actions",
                sql: "kind IN ('approve', 'reject', 'correct', 'second_approve', 'auto_approve')");

            migrationBuilder.CreateIndex(
                name: "ix_review_policies_tenant_id",
                schema: "verification",
                table: "review_policies",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_policies_tenant_id_record_type_version",
                schema: "verification",
                table: "review_policies",
                columns: new[] { "tenant_id", "record_type", "version" },
                unique: true);

            migrationBuilder.EnableTenantRls("verification", "review_policies");
            // A published policy version is never changed or removed (ADR-0037 §5): a change is a new version.
            migrationBuilder.Sql("REVOKE UPDATE, DELETE, TRUNCATE ON verification.review_policies FROM karry_app;");
            // Items created before policies existed ran under the defaults: version 0, due 24 h after creation.
            migrationBuilder.Sql("UPDATE verification.review_items SET due_at = created_at + interval '24 hours';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_policies",
                schema: "verification");

            migrationBuilder.DropIndex(
                name: "ix_review_items_tenant_id_status_due_at",
                schema: "verification",
                table: "review_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_review_actions_kind",
                schema: "verification",
                table: "review_actions");

            migrationBuilder.DropColumn(
                name: "due_at",
                schema: "verification",
                table: "review_items");

            migrationBuilder.DropColumn(
                name: "policy_version",
                schema: "verification",
                table: "review_items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_review_actions_kind",
                schema: "verification",
                table: "review_actions",
                sql: "kind IN ('approve', 'reject', 'correct', 'second_approve')");
        }
    }
}
