using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Integrity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Settlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_rule_settings_security_on",
                schema: "integrity",
                table: "rule_settings");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "fraud_suspected_at",
                schema: "integrity",
                table: "flags",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "settlement_comment",
                schema: "integrity",
                table: "flags",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "settlement_proposed_at",
                schema: "integrity",
                table: "flags",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "settlement_proposed_by",
                schema: "integrity",
                table: "flags",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "settlement_reason",
                schema: "integrity",
                table: "flags",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "integrity",
                table: "flags",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddCheckConstraint(
                name: "ck_rule_settings_security_on",
                schema: "integrity",
                table: "rule_settings",
                sql: "enabled OR rule NOT IN ('RULE-FO-05', 'RULE-FO-06', 'RULE-FO-07', 'RULE-FO-09', 'RULE-FO-10', 'RULE-FO-11')");

            migrationBuilder.CreateIndex(
                name: "ix_flags_tenant_id_rule_opened_at",
                schema: "integrity",
                table: "flags",
                columns: new[] { "tenant_id", "rule", "opened_at" },
                filter: "closed_at IS NULL AND fraud_suspected_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_flags_settlement",
                schema: "integrity",
                table: "flags",
                sql: "(settlement_reason IS NULL) = (settlement_proposed_at IS NULL) AND (settlement_reason IS NULL) = (settlement_comment IS NULL)");

            // An open flag changes by closing, by a settlement proposed or declined, and by RULE-FO-11 being raised about
            // it (once); a closed flag never changes; nothing is deleted (ADR-0036 §2, ADR-0037 §7).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION integrity.only_close() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                  mutable text[] := ARRAY['closed_at', 'close_reason', 'closed_by', 'settlement_reason', 'settlement_comment',
                                          'settlement_proposed_by', 'settlement_proposed_at', 'fraud_suspected_at'];
                BEGIN
                  IF TG_OP = 'UPDATE' AND OLD.closed_at IS NULL
                     AND (OLD.fraud_suspected_at IS NULL OR NEW.fraud_suspected_at IS NOT DISTINCT FROM OLD.fraud_suspected_at)
                     AND (to_jsonb(NEW) - mutable) = (to_jsonb(OLD) - mutable) THEN
                    RETURN NEW;
                  END IF;
                  RAISE EXCEPTION 'integrity.flags: a flag only closes (% refused)', TG_OP USING ERRCODE = '42501';
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION integrity.only_close() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF TG_OP = 'UPDATE' AND OLD.closed_at IS NULL AND NEW.closed_at IS NOT NULL
                     AND (to_jsonb(NEW) - 'closed_at' - 'close_reason' - 'closed_by') = (to_jsonb(OLD) - 'closed_at' - 'close_reason' - 'closed_by') THEN
                    RETURN NEW;
                  END IF;
                  RAISE EXCEPTION 'integrity.flags: a flag only closes (% refused)', TG_OP USING ERRCODE = '42501';
                END
                $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_rule_settings_security_on",
                schema: "integrity",
                table: "rule_settings");

            migrationBuilder.DropIndex(
                name: "ix_flags_tenant_id_rule_opened_at",
                schema: "integrity",
                table: "flags");

            migrationBuilder.DropCheckConstraint(
                name: "ck_flags_settlement",
                schema: "integrity",
                table: "flags");

            migrationBuilder.DropColumn(
                name: "fraud_suspected_at",
                schema: "integrity",
                table: "flags");

            migrationBuilder.DropColumn(
                name: "settlement_comment",
                schema: "integrity",
                table: "flags");

            migrationBuilder.DropColumn(
                name: "settlement_proposed_at",
                schema: "integrity",
                table: "flags");

            migrationBuilder.DropColumn(
                name: "settlement_proposed_by",
                schema: "integrity",
                table: "flags");

            migrationBuilder.DropColumn(
                name: "settlement_reason",
                schema: "integrity",
                table: "flags");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "integrity",
                table: "flags");

            migrationBuilder.AddCheckConstraint(
                name: "ck_rule_settings_security_on",
                schema: "integrity",
                table: "rule_settings",
                sql: "enabled OR rule NOT IN ('RULE-FO-05', 'RULE-FO-06', 'RULE-FO-07', 'RULE-FO-09', 'RULE-FO-10')");
        }
    }
}
