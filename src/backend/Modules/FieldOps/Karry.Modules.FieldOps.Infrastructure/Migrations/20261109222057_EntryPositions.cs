using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.FieldOps.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EntryPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "end_accuracy_m",
                schema: "fieldops",
                table: "shift_entries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "end_latitude",
                schema: "fieldops",
                table: "shift_entries",
                type: "numeric(8,6)",
                precision: 8,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "end_longitude",
                schema: "fieldops",
                table: "shift_entries",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "start_accuracy_m",
                schema: "fieldops",
                table: "shift_entries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "start_latitude",
                schema: "fieldops",
                table: "shift_entries",
                type: "numeric(8,6)",
                precision: 8,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "start_longitude",
                schema: "fieldops",
                table: "shift_entries",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_shift_entries_positions",
                schema: "fieldops",
                table: "shift_entries",
                sql: "(start_latitude IS NULL) = (start_longitude IS NULL) AND (start_latitude IS NULL) = (start_accuracy_m IS NULL) AND (end_latitude IS NULL) = (end_longitude IS NULL) AND (end_latitude IS NULL) = (end_accuracy_m IS NULL) AND coalesce(abs(start_latitude) <= 90 AND abs(start_longitude) <= 180 AND start_accuracy_m >= 0, true) AND coalesce(abs(end_latitude) <= 90 AND abs(end_longitude) <= 180 AND end_accuracy_m >= 0, true)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_shift_entries_positions",
                schema: "fieldops",
                table: "shift_entries");

            migrationBuilder.DropColumn(
                name: "end_accuracy_m",
                schema: "fieldops",
                table: "shift_entries");

            migrationBuilder.DropColumn(
                name: "end_latitude",
                schema: "fieldops",
                table: "shift_entries");

            migrationBuilder.DropColumn(
                name: "end_longitude",
                schema: "fieldops",
                table: "shift_entries");

            migrationBuilder.DropColumn(
                name: "start_accuracy_m",
                schema: "fieldops",
                table: "shift_entries");

            migrationBuilder.DropColumn(
                name: "start_latitude",
                schema: "fieldops",
                table: "shift_entries");

            migrationBuilder.DropColumn(
                name: "start_longitude",
                schema: "fieldops",
                table: "shift_entries");
        }
    }
}
