using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Assets.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Labels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "label_token",
                schema: "assets",
                table: "assets",
                type: "character(26)",
                fixedLength: true,
                maxLength: 26,
                nullable: true);

            // Hand-written (reviewed): assets registered before labels get a random token like new ones (26 base32
            // characters from pgcrypto's random bytes; the subquery refers to the row, so each asset draws its own).
            migrationBuilder.Sql("""
                UPDATE assets.assets AS a SET label_token = (
                  SELECT string_agg(substr('ABCDEFGHIJKLMNOPQRSTUVWXYZ234567', get_byte(r.bytes, i) % 32 + 1, 1), '' ORDER BY i)
                  FROM (SELECT gen_random_bytes(26) AS bytes WHERE a.id IS NOT NULL) AS r, generate_series(0, 25) AS i);
                ALTER TABLE assets.assets ALTER COLUMN label_token SET NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_assets_label_token",
                schema: "assets",
                table: "assets",
                column: "label_token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_assets_label_token",
                schema: "assets",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "label_token",
                schema: "assets",
                table: "assets");
        }
    }
}
