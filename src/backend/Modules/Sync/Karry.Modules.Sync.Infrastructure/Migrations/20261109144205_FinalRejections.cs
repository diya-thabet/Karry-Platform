using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Sync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FinalRejections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "code",
                schema: "sync",
                table: "processed_commands",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "code",
                schema: "sync",
                table: "processed_commands");
        }
    }
}
