using System;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Karry.Modules.Organization.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Shifts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "non_working_days",
                schema: "organization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_non_working_days", x => x.id);
                    table.ForeignKey(
                        name: "fk_non_working_days_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "organization",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shift_instances",
                schema: "organization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    shift_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    shift_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shift_instances", x => x.id);
                    table.CheckConstraint("ck_shift_instances_order", "ends_at > starts_at");
                    table.ForeignKey(
                        name: "fk_shift_instances_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "organization",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shift_patterns",
                schema: "organization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    shifts = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shift_patterns", x => x.id);
                    table.ForeignKey(
                        name: "fk_shift_patterns_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "organization",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_non_working_days_site_id",
                schema: "organization",
                table: "non_working_days",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_non_working_days_tenant_id",
                schema: "organization",
                table: "non_working_days",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_non_working_days_tenant_id_site_id_date",
                schema: "organization",
                table: "non_working_days",
                columns: new[] { "tenant_id", "site_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shift_instances_site_id_starts_at",
                schema: "organization",
                table: "shift_instances",
                columns: new[] { "site_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_shift_instances_tenant_id",
                schema: "organization",
                table: "shift_instances",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_shift_instances_tenant_id_site_id_date_shift_code",
                schema: "organization",
                table: "shift_instances",
                columns: new[] { "tenant_id", "site_id", "date", "shift_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shift_patterns_site_id",
                schema: "organization",
                table: "shift_patterns",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_shift_patterns_tenant_id",
                schema: "organization",
                table: "shift_patterns",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_shift_patterns_tenant_id_site_id_effective_from",
                schema: "organization",
                table: "shift_patterns",
                columns: new[] { "tenant_id", "site_id", "effective_from" },
                unique: true);

            migrationBuilder.EnableTenantRls("organization", "shift_patterns");
            migrationBuilder.EnableTenantRls("organization", "non_working_days");
            migrationBuilder.EnableTenantRls("organization", "shift_instances");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "non_working_days",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "shift_instances",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "shift_patterns",
                schema: "organization");
        }
    }
}
