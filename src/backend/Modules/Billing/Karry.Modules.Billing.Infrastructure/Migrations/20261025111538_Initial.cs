using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "billing",
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
                name: "plans",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    features = table.Column<string>(type: "jsonb", nullable: false),
                    limits = table.Column<string>(type: "jsonb", nullable: false),
                    prices = table.Column<string>(type: "jsonb", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "processed_messages",
                schema: "billing",
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
                name: "ix_outbox_messages_aggregate_key_occurred_at",
                schema: "billing",
                table: "outbox_messages",
                columns: new[] { "aggregate_key", "occurred_at" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_occurred_at_id",
                schema: "billing",
                table: "outbox_messages",
                columns: new[] { "occurred_at", "id" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_plans_code_version",
                schema: "billing",
                table: "plans",
                columns: new[] { "code", "version" },
                unique: true);

            // Version 1 of the four plans of the business model §2.3: ILLUSTRATIVE values until pricing validation
            // (WP-P0-07, owner input N-01). Final values are published as version 2 through the API, not here.
            // Annual = 10 × monthly (2 months free). Enterprise is quoted by sales: no prices, no limits.
            migrationBuilder.Sql("""
                INSERT INTO billing.plans (id, code, version, name, features, limits, prices, published_at) VALUES
                ('019a1a4c-0000-7000-8000-000000000001', 'starter', 1, 'Starter',
                 '["field-operations","fuel","integrity-dashboard","inventory","weighbridge"]',
                 '{"assets-per-site":20,"edge-agents-per-site":0,"office-users":5,"retention-years":3}',
                 '[{"currency":"EUR","period":"monthly","perSite":249,"extraAsset":6},{"currency":"EUR","period":"annual","perSite":2490,"extraAsset":60}]',
                 '2026-10-25T00:00:00Z'),
                ('019a1a4c-0000-7000-8000-000000000002', 'professional', 1, 'Professional',
                 '["api.read","field-operations","fuel","integrity-dashboard","inventory","inventory.multi-store","maintenance","weighbridge"]',
                 '{"assets-per-site":50,"edge-agents-per-site":0,"office-users":15,"retention-years":7}',
                 '[{"currency":"EUR","period":"monthly","perSite":590,"extraAsset":5},{"currency":"EUR","period":"annual","perSite":5900,"extraAsset":50}]',
                 '2026-10-25T00:00:00Z'),
                ('019a1a4c-0000-7000-8000-000000000003', 'business', 1, 'Business',
                 '["ai.copilot","api.read","api.write","edge.agent","field-operations","finance","fuel","integrity-dashboard","inventory","inventory.multi-store","maintenance","plant","sso","weighbridge"]',
                 '{"assets-per-site":120,"edge-agents-per-site":1,"office-users":40,"retention-years":10}',
                 '[{"currency":"EUR","period":"monthly","perSite":990,"extraAsset":4},{"currency":"EUR","period":"annual","perSite":9900,"extraAsset":40}]',
                 '2026-10-25T00:00:00Z'),
                ('019a1a4c-0000-7000-8000-000000000004', 'enterprise', 1, 'Enterprise',
                 '["ai.copilot","api.read","api.write","dedicated-hosting","edge.agent","field-operations","finance","fuel","integrity-dashboard","inventory","inventory.multi-store","maintenance","plant","sso","telematics","weighbridge"]',
                 '{}', '[]', '2026-10-25T00:00:00Z');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "plans",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "processed_messages",
                schema: "billing");
        }
    }
}
