using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Trust.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SignatureEnvelopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "signature_envelopes",
                schema: "audit",
                columns: table => new
                {
                    envelope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    envelope_hash = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    canonical = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_signature = table.Column<string>(type: "character varying(86)", maxLength: 86, nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signature_envelopes", x => x.envelope_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_signature_envelopes_tenant_id",
                schema: "audit",
                table: "signature_envelopes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_signature_envelopes_tenant_id_received_at",
                schema: "audit",
                table: "signature_envelopes",
                columns: new[] { "tenant_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_signature_envelopes_tenant_id_record_type_record_id",
                schema: "audit",
                table: "signature_envelopes",
                columns: new[] { "tenant_id", "record_type", "record_id" });

            // Hand-written (reviewed): tenant data, append-only like the ledger (ADR-0033 §4, TC-P2-SIG-007). The
            // countersignature is a separate row, written once, so nothing here is ever updated.
            migrationBuilder.EnableTenantRls("audit", "signature_envelopes");
            migrationBuilder.Sql("""
                CREATE FUNCTION audit.reject_envelope_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  RAISE EXCEPTION 'audit.signature_envelopes is append-only (% refused)', TG_OP USING ERRCODE = '42501';
                END
                $$;
                CREATE TRIGGER signature_envelopes_no_update_delete BEFORE UPDATE OR DELETE ON audit.signature_envelopes
                  FOR EACH ROW EXECUTE FUNCTION audit.reject_envelope_change();
                CREATE TRIGGER signature_envelopes_no_truncate BEFORE TRUNCATE ON audit.signature_envelopes
                  FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_envelope_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON audit.signature_envelopes FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "signature_envelopes",
                schema: "audit");

            migrationBuilder.Sql("DROP FUNCTION audit.reject_envelope_change();");
        }
    }
}
