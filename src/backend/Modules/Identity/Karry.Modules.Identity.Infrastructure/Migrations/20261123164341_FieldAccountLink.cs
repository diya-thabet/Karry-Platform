using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FieldAccountLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "linked_field_user_id",
                schema: "identity",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_tenant_id_linked_field_user_id",
                schema: "identity",
                table: "users",
                columns: new[] { "tenant_id", "linked_field_user_id" },
                unique: true,
                filter: "linked_field_user_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_tenant_id_linked_field_user_id",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "linked_field_user_id",
                schema: "identity",
                table: "users");
        }
    }
}
