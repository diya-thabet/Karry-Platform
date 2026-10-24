using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SignInFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "import_cursors",
                schema: "identity",
                columns: table => new
                {
                    source = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    position = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_cursors", x => x.source);
                });

            migrationBuilder.CreateTable(
                name: "sign_in_failures",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sign_in_failures", x => x.id);
                    table.ForeignKey(
                        name: "fk_sign_in_failures_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sign_in_failures_tenant_id",
                schema: "identity",
                table: "sign_in_failures",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sign_in_failures_user_id_occurred_at",
                schema: "identity",
                table: "sign_in_failures",
                columns: new[] { "user_id", "occurred_at" });

            // Hand-written (reviewed): sign-in failures are tenant data; import_cursors is platform bookkeeping (global table).
            migrationBuilder.EnableTenantRls("identity", "sign_in_failures");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_cursors",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "sign_in_failures",
                schema: "identity");
        }
    }
}
