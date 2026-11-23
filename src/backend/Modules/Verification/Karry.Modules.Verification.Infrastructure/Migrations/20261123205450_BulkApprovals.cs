using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Verification.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BulkApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "batch_id",
                schema: "verification",
                table: "review_actions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "review_batches",
                schema: "verification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    item_ids = table.Column<string>(type: "jsonb", nullable: false),
                    record_hashes = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_batches", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_review_batches_tenant_id",
                schema: "verification",
                table: "review_batches",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_batches_tenant_id_at",
                schema: "verification",
                table: "review_batches",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.EnableTenantRls("verification", "review_batches");
            // A bulk approval is a decision: never changed or removed (ADR-0037 §2).
            migrationBuilder.Sql("REVOKE UPDATE, DELETE, TRUNCATE ON verification.review_batches FROM karry_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_batches",
                schema: "verification");

            migrationBuilder.DropColumn(
                name: "batch_id",
                schema: "verification",
                table: "review_actions");
        }
    }
}
