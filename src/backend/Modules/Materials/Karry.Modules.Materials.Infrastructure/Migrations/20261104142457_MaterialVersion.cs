using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Materials.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MaterialVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "materials",
                table: "materials",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "materials",
                table: "materials");
        }
    }
}
