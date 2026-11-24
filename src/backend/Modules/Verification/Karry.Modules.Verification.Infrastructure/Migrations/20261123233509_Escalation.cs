using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Verification.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Escalation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_review_actions_kind",
                schema: "verification",
                table: "review_actions");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "escalated_at",
                schema: "verification",
                table: "review_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_review_actions_kind",
                schema: "verification",
                table: "review_actions",
                sql: "kind IN ('approve', 'reject', 'correct', 'second_approve', 'auto_approve', 'escalate')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_review_actions_kind",
                schema: "verification",
                table: "review_actions");

            migrationBuilder.DropColumn(
                name: "escalated_at",
                schema: "verification",
                table: "review_items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_review_actions_kind",
                schema: "verification",
                table: "review_actions",
                sql: "kind IN ('approve', 'reject', 'correct', 'second_approve', 'auto_approve')");
        }
    }
}
