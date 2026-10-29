using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SupportAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "support_consents",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_consents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "support_sessions",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    operator_subject = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    operator_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    consent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_sessions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_support_consents_tenant_id",
                schema: "identity",
                table: "support_consents",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_support_consents_tenant_id_code_hash",
                schema: "identity",
                table: "support_consents",
                columns: new[] { "tenant_id", "code_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_support_sessions_tenant_id",
                schema: "identity",
                table: "support_sessions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_support_sessions_tenant_id_started_at",
                schema: "identity",
                table: "support_sessions",
                columns: new[] { "tenant_id", "started_at" });

            migrationBuilder.EnableTenantRls("identity", "support_consents");
            migrationBuilder.EnableTenantRls("identity", "support_sessions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "support_consents",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "support_sessions",
                schema: "identity");
        }
    }
}
