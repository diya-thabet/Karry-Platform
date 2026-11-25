using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Notifications.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    link = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "notifications",
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
                schema: "notifications",
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
                name: "ix_notifications_tenant_id",
                schema: "notifications",
                table: "notifications",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_created_at",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_recipient_id_id",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "tenant_id", "recipient_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_recipient_id_read_at",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "tenant_id", "recipient_id", "read_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_source_event_id_recipient_id",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "tenant_id", "source_event_id", "recipient_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_aggregate_key_occurred_at",
                schema: "notifications",
                table: "outbox_messages",
                columns: new[] { "aggregate_key", "occurred_at" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_occurred_at_id",
                schema: "notifications",
                table: "outbox_messages",
                columns: new[] { "occurred_at", "id" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            // A person's notices, in their company only (ADR-0039 §1); the Workers delete them after 90 days.
            migrationBuilder.EnableTenantRls("notifications", "notifications");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "processed_messages",
                schema: "notifications");
        }
    }
}
