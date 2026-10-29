using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Trust.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Verifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "verifications",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    verified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    valid = table.Column<bool>(type: "boolean", nullable: false),
                    entries_checked = table.Column<long>(type: "bigint", nullable: false),
                    first_broken_sequence = table.Column<long>(type: "bigint", nullable: true),
                    problem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    head_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    anchors_checked = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_verifications", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_verifications_tenant_id",
                schema: "audit",
                table: "verifications",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_verifications_tenant_id_verified_at",
                schema: "audit",
                table: "verifications",
                columns: new[] { "tenant_id", "verified_at" });

            // Hand-written (reviewed): tenant data; a past integrity check is evidence of what was found, so it is
            // append-only like the ledger (US-P1-AUD-03 AC2, ADR-0029 §2).
            migrationBuilder.EnableTenantRls("audit", "verifications");
            migrationBuilder.Sql("""
                CREATE FUNCTION audit.reject_verification_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  RAISE EXCEPTION 'audit.verifications is append-only (% refused)', TG_OP USING ERRCODE = '42501';
                END
                $$;
                CREATE TRIGGER verifications_no_update_delete BEFORE UPDATE OR DELETE ON audit.verifications
                  FOR EACH ROW EXECUTE FUNCTION audit.reject_verification_change();
                CREATE TRIGGER verifications_no_truncate BEFORE TRUNCATE ON audit.verifications
                  FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_verification_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON audit.verifications FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION audit.reject_verification_change() CASCADE;");
            migrationBuilder.DropTable(
                name: "verifications",
                schema: "audit");
        }
    }
}
