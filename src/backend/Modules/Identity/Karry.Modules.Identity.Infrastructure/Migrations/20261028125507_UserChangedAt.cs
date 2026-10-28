using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UserChangedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "changed_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.CreateIndex(
                name: "ix_users_tenant_id_changed_at",
                schema: "identity",
                table: "users",
                columns: new[] { "tenant_id", "changed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_tenant_id_changed_at",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "changed_at",
                schema: "identity",
                table: "users");
        }
    }
}
