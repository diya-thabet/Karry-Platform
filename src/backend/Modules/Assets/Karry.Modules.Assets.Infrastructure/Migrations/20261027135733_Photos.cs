using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Assets.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Photos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "photo_bytes",
                schema: "assets",
                table: "assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "photo_content_type",
                schema: "assets",
                table: "assets",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "photo_sha256",
                schema: "assets",
                table: "assets",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "photo_bytes",
                schema: "assets",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "photo_content_type",
                schema: "assets",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "photo_sha256",
                schema: "assets",
                table: "assets");
        }
    }
}
