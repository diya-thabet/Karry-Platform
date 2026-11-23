using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Verification.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "verification");

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "verification",
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
                schema: "verification",
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
                name: "review_items",
                schema: "verification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    record_hash = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    produced_by = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    covered_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    covered_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    flags = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_items", x => x.id);
                    table.CheckConstraint("ck_review_items_status", "status IN ('pending', 'approved', 'rejected', 'corrected', 'awaiting_second')");
                    table.CheckConstraint("ck_review_items_version", "record_version >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_aggregate_key_occurred_at",
                schema: "verification",
                table: "outbox_messages",
                columns: new[] { "aggregate_key", "occurred_at" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_occurred_at_id",
                schema: "verification",
                table: "outbox_messages",
                columns: new[] { "occurred_at", "id" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_review_items_tenant_id",
                schema: "verification",
                table: "review_items",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_items_tenant_id_record_type_record_id_record_version",
                schema: "verification",
                table: "review_items",
                columns: new[] { "tenant_id", "record_type", "record_id", "record_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_items_tenant_id_site_id_status_created_at",
                schema: "verification",
                table: "review_items",
                columns: new[] { "tenant_id", "site_id", "status", "created_at" });

            migrationBuilder.EnableTenantRls("verification", "review_items");
            // A review item is never deleted (ADR-0037 §2): its state changes only through ledgered decisions.
            migrationBuilder.Sql("REVOKE DELETE, TRUNCATE ON verification.review_items FROM karry_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "verification");

            migrationBuilder.DropTable(
                name: "processed_messages",
                schema: "verification");

            migrationBuilder.DropTable(
                name: "review_items",
                schema: "verification");
        }
    }
}
