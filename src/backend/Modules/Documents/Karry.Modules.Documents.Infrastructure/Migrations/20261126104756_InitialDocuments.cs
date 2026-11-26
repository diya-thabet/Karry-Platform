using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Documents.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "documents");

            migrationBuilder.CreateTable(
                name: "envelopes",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shift_date = table.Column<DateOnly>(type: "date", nullable: false),
                    shift_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    pdf_hash = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    signature = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    signature_key_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_envelopes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "documents",
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
                schema: "documents",
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

            migrationBuilder.CreateIndex(
                name: "ix_envelopes_tenant_id",
                schema: "documents",
                table: "envelopes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_envelopes_tenant_id_signed_at",
                schema: "documents",
                table: "envelopes",
                columns: new[] { "tenant_id", "signed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_envelopes_tenant_id_site_id_shift_date_shift_code",
                schema: "documents",
                table: "envelopes",
                columns: new[] { "tenant_id", "site_id", "shift_date", "shift_code" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_aggregate_key_occurred_at",
                schema: "documents",
                table: "outbox_messages",
                columns: new[] { "aggregate_key", "occurred_at" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_occurred_at_id",
                schema: "documents",
                table: "outbox_messages",
                columns: new[] { "occurred_at", "id" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            // Tenant isolation (ADR-0006): each company sees only its own documents.
            migrationBuilder.EnableTenantRls("documents", "envelopes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "envelopes",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "processed_messages",
                schema: "documents");
        }
    }
}
