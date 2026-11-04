using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Organization.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SiteVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "organization",
                table: "sites",
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
                schema: "organization",
                table: "sites");
        }
    }
}
