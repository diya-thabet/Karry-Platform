using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Trust.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LedgerSubjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ledger_subjects",
                schema: "audit",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    entity_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_subjects", x => new { x.tenant_id, x.seq });
                });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_subjects_tenant_id",
                schema: "audit",
                table: "ledger_subjects",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_subjects_tenant_id_actor_seq",
                schema: "audit",
                table: "ledger_subjects",
                columns: new[] { "tenant_id", "actor", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_subjects_tenant_id_entity_type_entity_id_seq",
                schema: "audit",
                table: "ledger_subjects",
                columns: new[] { "tenant_id", "entity_type", "entity_id", "seq" });

            // Hand-written (reviewed): tenant data, append-only like the entries it indexes, and only for entries that
            // exist (ADR-0029 §1).
            migrationBuilder.EnableTenantRls("audit", "ledger_subjects");
            migrationBuilder.Sql("""
                ALTER TABLE audit.ledger_subjects ADD CONSTRAINT fk_ledger_subjects_entry
                  FOREIGN KEY (tenant_id, seq) REFERENCES audit.ledger_entries (tenant_id, seq);
                CREATE FUNCTION audit.reject_subject_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  RAISE EXCEPTION 'audit.ledger_subjects is append-only (% refused)', TG_OP USING ERRCODE = '42501';
                END
                $$;
                CREATE TRIGGER ledger_subjects_no_update_delete BEFORE UPDATE OR DELETE ON audit.ledger_subjects
                  FOR EACH ROW EXECUTE FUNCTION audit.reject_subject_change();
                CREATE TRIGGER ledger_subjects_no_truncate BEFORE TRUNCATE ON audit.ledger_subjects
                  FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_subject_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON audit.ledger_subjects FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION audit.reject_subject_change() CASCADE;");
            migrationBuilder.DropTable(
                name: "ledger_subjects",
                schema: "audit");
        }
    }
}
