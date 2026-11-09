using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Assets.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AssetVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "assets",
                table: "assets",
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
                schema: "assets",
                table: "assets");
        }
    }
}
