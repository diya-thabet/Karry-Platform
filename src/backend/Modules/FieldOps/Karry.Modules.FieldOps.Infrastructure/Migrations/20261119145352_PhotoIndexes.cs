using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.FieldOps.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PhotoIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_shift_entries_tenant_id_end_photo",
                schema: "fieldops",
                table: "shift_entries",
                columns: new[] { "tenant_id", "end_photo" });

            migrationBuilder.CreateIndex(
                name: "ix_shift_entries_tenant_id_start_photo",
                schema: "fieldops",
                table: "shift_entries",
                columns: new[] { "tenant_id", "start_photo" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_shift_entries_tenant_id_end_photo",
                schema: "fieldops",
                table: "shift_entries");

            migrationBuilder.DropIndex(
                name: "ix_shift_entries_tenant_id_start_photo",
                schema: "fieldops",
                table: "shift_entries");
        }
    }
}
