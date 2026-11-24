using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Verification.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class QueueRisk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "risk",
                schema: "verification",
                table: "review_items",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_review_items_tenant_id_status_risk_id",
                schema: "verification",
                table: "review_items",
                columns: new[] { "tenant_id", "status", "risk", "id" });

            // Items already waiting rank as the domain ranks new ones (ADR-0038 §1): escalated, then flagged.
            migrationBuilder.Sql("""
                UPDATE verification.review_items
                SET risk = CASE WHEN escalated_at IS NOT NULL THEN 2 WHEN jsonb_array_length(flags) > 0 THEN 1 ELSE 0 END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_review_items_tenant_id_status_risk_id",
                schema: "verification",
                table: "review_items");

            migrationBuilder.DropColumn(
                name: "risk",
                schema: "verification",
                table: "review_items");
        }
    }
}
