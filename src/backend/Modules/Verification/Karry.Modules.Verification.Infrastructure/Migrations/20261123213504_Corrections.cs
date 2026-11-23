using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Verification.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Corrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_review_actions_kind",
                schema: "verification",
                table: "review_actions");

            migrationBuilder.AddColumn<string>(
                name: "corrected_by",
                schema: "verification",
                table: "review_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_review_actions_kind",
                schema: "verification",
                table: "review_actions",
                sql: "kind IN ('approve', 'reject', 'correct', 'second_approve')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_review_actions_kind",
                schema: "verification",
                table: "review_actions");

            migrationBuilder.DropColumn(
                name: "corrected_by",
                schema: "verification",
                table: "review_items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_review_actions_kind",
                schema: "verification",
                table: "review_actions",
                sql: "kind IN ('approve', 'reject')");
        }
    }
}
