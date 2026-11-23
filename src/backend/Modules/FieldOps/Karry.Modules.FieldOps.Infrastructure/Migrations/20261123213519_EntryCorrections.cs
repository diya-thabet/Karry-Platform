using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.FieldOps.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EntryCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "corrected_by",
                schema: "fieldops",
                table: "shift_entries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "correction_reason",
                schema: "fieldops",
                table: "shift_entries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "corrected_by",
                schema: "fieldops",
                table: "shift_entries");

            migrationBuilder.DropColumn(
                name: "correction_reason",
                schema: "fieldops",
                table: "shift_entries");
        }
    }
}
