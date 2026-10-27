using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Karry.Modules.Assets.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "assets");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "assets",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    make = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    model = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    serial_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    plate = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    year = table.Column<int>(type: "integer", nullable: true),
                    acquired_on = table.Column<DateOnly>(type: "date", nullable: true),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disposed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    search_text = table.Column<string>(type: "text", nullable: true, computedColumnSql: "lower(code || ' ' || name || ' ' || coalesce(make, '') || ' ' || coalesce(model, '') || ' ' || coalesce(serial_number, '') || ' ' || coalesce(plate, ''))", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                    table.CheckConstraint("ck_assets_disposed", "(status = 'Disposed') = (disposed_at IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    aggregate_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dead_lettered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "processed_messages",
                schema: "assets",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handler = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_messages", x => new { x.message_id, x.handler });
                });

            migrationBuilder.CreateTable(
                name: "site_assignments",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_site_assignments_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "assets",
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assets_search_text",
                schema: "assets",
                table: "assets",
                column: "search_text")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id",
                schema: "assets",
                table: "assets",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_code",
                schema: "assets",
                table: "assets",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_site_id",
                schema: "assets",
                table: "assets",
                columns: new[] { "tenant_id", "site_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_aggregate_key_occurred_at",
                schema: "assets",
                table: "outbox_messages",
                columns: new[] { "aggregate_key", "occurred_at" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_occurred_at_id",
                schema: "assets",
                table: "outbox_messages",
                columns: new[] { "occurred_at", "id" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_site_assignments_asset_id",
                schema: "assets",
                table: "site_assignments",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_site_assignments_tenant_id",
                schema: "assets",
                table: "site_assignments",
                column: "tenant_id");

            // Hand-written (reviewed): assets and their site history are tenant data; the history is append-only (ADR-0026).
            migrationBuilder.EnableTenantRls("assets", "assets");
            migrationBuilder.EnableTenantRls("assets", "site_assignments");
            migrationBuilder.Sql("""
                CREATE FUNCTION assets.reject_history_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  RAISE EXCEPTION '%.% is append-only (% refused)', TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP USING ERRCODE = '42501';
                END
                $$;
                CREATE TRIGGER site_assignments_no_update_delete BEFORE UPDATE OR DELETE ON assets.site_assignments
                  FOR EACH ROW EXECUTE FUNCTION assets.reject_history_change();
                CREATE TRIGGER site_assignments_no_truncate BEFORE TRUNCATE ON assets.site_assignments
                  FOR EACH STATEMENT EXECUTE FUNCTION assets.reject_history_change();
                REVOKE UPDATE, DELETE, TRUNCATE ON assets.site_assignments FROM karry_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION assets.reject_history_change() CASCADE;");
            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "processed_messages",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "site_assignments",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "assets",
                schema: "assets");
        }
    }
}
