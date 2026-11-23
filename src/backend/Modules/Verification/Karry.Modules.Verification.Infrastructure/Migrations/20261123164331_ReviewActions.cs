using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Verification.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReviewActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "review_actions",
                schema: "verification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reason_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    record_hash = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_actions", x => x.id);
                    table.CheckConstraint("ck_review_actions_kind", "kind IN ('approve', 'reject')");
                    table.CheckConstraint("ck_review_actions_reason", "reason_code IS NULL OR reason_code IN ('wrong_reading', 'photo_unreadable', 'wrong_machine', 'wrong_shift', 'duplicate', 'other')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_review_actions_tenant_id",
                schema: "verification",
                table: "review_actions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_actions_tenant_id_item_id_at",
                schema: "verification",
                table: "review_actions",
                columns: new[] { "tenant_id", "item_id", "at" });

            migrationBuilder.EnableTenantRls("verification", "review_actions");
            // A decision is never changed or removed (ADR-0037 §2): the actions are append-only.
            migrationBuilder.Sql("REVOKE UPDATE, DELETE, TRUNCATE ON verification.review_actions FROM karry_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_actions",
                schema: "verification");
        }
    }
}
