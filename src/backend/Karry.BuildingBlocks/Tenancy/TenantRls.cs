using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Karry.BuildingBlocks.Tenancy;

/// <summary>
/// Row-level security for a tenant-owned table (US-P1-TEN-02 AC1, ADR-0004). Call from the migration that creates the
/// table: <c>migrationBuilder.EnableTenantRls("assets", "assets");</c>. Generates ENABLE + FORCE (so even the owner is
/// filtered, TC-P0-RLS-008) and one policy for USING and WITH CHECK on
/// <c>nullif(current_setting('app.tenant_id', true), '')::uuid</c> (the form proven by spike WP-P0-12: a plain
/// <c>::uuid</c> cast throws on a reused pooled session).
/// </summary>
public static partial class TenantRls
{
    public const string PolicyName = "tenant_isolation";

    private const string CurrentTenant = "nullif(current_setting('app.tenant_id', true), '')::uuid";

    /// <summary>SQL that enables tenant RLS on <c>schema.table</c> (column <c>tenant_id</c>).</summary>
    /// <param name="schema">Module schema (lowercase identifier).</param>
    /// <param name="table">Table name (lowercase identifier).</param>
    /// <param name="allowPlatformRows">
    /// Also allow rows with a NULL tenant, visible only when no tenant is set (platform-level records such as
    /// idempotency entries of operator endpoints). A tenant never sees them; platform work never sees tenant rows.
    /// </param>
    public static string EnableSql(string schema, string table, bool allowPlatformRows = false)
    {
        var target = $"{Identifier(schema)}.{Identifier(table)}";
        var predicate = allowPlatformRows
            ? $"tenant_id IS NOT DISTINCT FROM {CurrentTenant}"
            : $"tenant_id = {CurrentTenant}";
        return $"""
            ALTER TABLE {target} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {target} FORCE ROW LEVEL SECURITY;
            CREATE POLICY {PolicyName} ON {target} USING ({predicate}) WITH CHECK ({predicate});
            """;
    }

    public static MigrationBuilder EnableTenantRls(this MigrationBuilder migrationBuilder, string schema, string table, bool allowPlatformRows = false)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(EnableSql(schema, table, allowPlatformRows));
        return migrationBuilder;
    }

    // Schema and table names are code constants; still validated, because they are spliced into DDL.
    private static string Identifier(string name) =>
        IdentifierPattern().IsMatch(name) ? name : throw new ArgumentException($"'{name}' is not a valid lowercase identifier.", nameof(name));

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex IdentifierPattern();
}
