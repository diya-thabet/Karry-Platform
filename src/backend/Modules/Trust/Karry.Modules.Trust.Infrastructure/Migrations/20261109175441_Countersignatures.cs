using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Trust.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Countersignatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "server_keys",
                schema: "audit",
                columns: table => new
                {
                    key_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    public_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_keys", x => x.key_id);
                });

            migrationBuilder.CreateTable(
                name: "envelope_countersignatures",
                schema: "audit",
                columns: table => new
                {
                    envelope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    signature = table.Column<string>(type: "character varying(86)", maxLength: 86, nullable: false),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_envelope_countersignatures", x => x.envelope_id);
                    table.ForeignKey(
                        name: "fk_envelope_countersignatures_server_keys_key_id",
                        column: x => x.key_id,
                        principalSchema: "audit",
                        principalTable: "server_keys",
                        principalColumn: "key_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_envelope_countersignatures_signature_envelopes_envelope_id",
                        column: x => x.envelope_id,
                        principalSchema: "audit",
                        principalTable: "signature_envelopes",
                        principalColumn: "envelope_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_envelope_countersignatures_key_id",
                schema: "audit",
                table: "envelope_countersignatures",
                column: "key_id");

            migrationBuilder.CreateIndex(
                name: "ix_envelope_countersignatures_tenant_id",
                schema: "audit",
                table: "envelope_countersignatures",
                column: "tenant_id");

            // Hand-written (reviewed): countersignatures are tenant data under RLS; both tables are append-only
            // (ADR-0033 §3–§4): a countersignature is written once, and a published key never changes, so old
            // countersignatures stay verifiable. server_keys is global (public keys, no tenant data; declared in
            // deploy/database/global-reference-tables.txt).
            migrationBuilder.EnableTenantRls("audit", "envelope_countersignatures");
            migrationBuilder.Sql("""
                CREATE FUNCTION audit.reject_append_only_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  RAISE EXCEPTION 'audit.% is append-only (% refused)', TG_TABLE_NAME, TG_OP USING ERRCODE = '42501';
                END
                $$;
                CREATE TRIGGER envelope_countersignatures_no_update_delete BEFORE UPDATE OR DELETE ON audit.envelope_countersignatures
                  FOR EACH ROW EXECUTE FUNCTION audit.reject_append_only_change();
                CREATE TRIGGER envelope_countersignatures_no_truncate BEFORE TRUNCATE ON audit.envelope_countersignatures
                  FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_append_only_change();
                CREATE TRIGGER server_keys_no_update_delete BEFORE UPDATE OR DELETE ON audit.server_keys
                  FOR EACH ROW EXECUTE FUNCTION audit.reject_append_only_change();
                CREATE TRIGGER server_keys_no_truncate BEFORE TRUNCATE ON audit.server_keys
                  FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_append_only_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON audit.envelope_countersignatures, audit.server_keys FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "envelope_countersignatures",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "server_keys",
                schema: "audit");

            migrationBuilder.Sql("DROP FUNCTION audit.reject_append_only_change();");
        }
    }
}
