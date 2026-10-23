using Npgsql;

namespace Karry.Testing;

/// <summary>
/// Migration linter (US-P1-TEN-02 AC5, TC-P1-TEN-004): inspects the <b>migrated database</b> (not the C# code) and
/// reports every table of a Karry schema that lacks tenant row-level security and is not declared in
/// <c>deploy/database/global-reference-tables.txt</c>. A stale or reason-less declaration is a violation too.
/// </summary>
public static class MigrationLinter
{
    public const string DeclarationsFile = "deploy/database/global-reference-tables.txt";

    private static readonly string[] SystemSchemas = ["public", "information_schema", "pg_catalog", "pg_toast", "timescaledb_information", "timescaledb_experimental", "_timescaledb_catalog", "_timescaledb_internal", "_timescaledb_config", "_timescaledb_cache", "_timescaledb_functions", "toolkit_experimental", "topology", "tiger", "tiger_data"];

    private const string CurrentTenant = "(NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid";
    private const string TenantPolicy = "(tenant_id = " + CurrentTenant + ")";
    private const string PlatformRowsPolicy = "(NOT (tenant_id IS DISTINCT FROM " + CurrentTenant + "))";

    public sealed record Declaration(string Schema, string Table, string Kind, string Reason);

    public static IReadOnlyList<Declaration> ReadDeclarations(string path)
    {
        var declarations = new List<Declaration>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            var name = parts[0].Split('.');
            declarations.Add(new Declaration(name[0], name.Length > 1 ? name[1] : string.Empty,
                parts.Length > 1 ? parts[1] : string.Empty, parts.Length > 2 ? parts[2] : string.Empty));
        }

        return declarations;
    }

    public static async Task<IReadOnlyList<string>> LintAsync(string adminConnectionString, IReadOnlyList<Declaration> declarations)
    {
        var violations = new List<string>();
        foreach (var bad in declarations.Where(d => d.Table.Length == 0 || d.Kind is not ("global" or "platform-rows") || d.Reason.Length < 10))
        {
            violations.Add($"{bad.Schema}.{bad.Table}: declaration needs 'schema.table | global|platform-rows | reason'.");
        }

        var tables = await ReadTablesAsync(adminConnectionString).ConfigureAwait(false);
        foreach (var table in tables)
        {
            var declaration = declarations.FirstOrDefault(d => d.Table == table.Name && (d.Schema == table.Schema || d.Schema == "*"));
            var name = $"{table.Schema}.{table.Name}";
            switch (declaration?.Kind)
            {
                case "global":
                    break;
                case "platform-rows":
                    Require(table.TenantColumn is "uuid" or "uuid?", $"{name}: platform-rows table needs a uuid tenant_id column.");
                    RequireRls(table, name, platformRows: true);
                    break;
                default:
                    Require(table.TenantColumn == "uuid", $"{name}: tenant table needs `tenant_id uuid NOT NULL` (or a declaration in {DeclarationsFile}).");
                    RequireRls(table, name, platformRows: false);
                    break;
            }
        }

        foreach (var declaration in declarations.Where(d => d.Table.Length > 0))
        {
            var exists = tables.Any(t => t.Name == declaration.Table && (declaration.Schema == "*" || t.Schema == declaration.Schema));
            Require(exists, $"{declaration.Schema}.{declaration.Table}: declared in {DeclarationsFile} but no such table (stale entry).");
        }

        return violations;

        void Require(bool condition, string message)
        {
            if (!condition)
            {
                violations.Add(message);
            }
        }

        void RequireRls(TableInfo table, string name, bool platformRows)
        {
            Require(table.RlsEnabled, $"{name}: row-level security not enabled.");
            Require(table.RlsForced, $"{name}: row-level security not FORCEd (the owner would bypass it).");
            // Exactly the policies EnableTenantRls creates, as PostgreSQL stores (deparses) them.
            var expected = platformRows ? PlatformRowsPolicy : TenantPolicy;
            var policyOk = table.Using == expected && table.WithCheck == expected;
            Require(policyOk, $"{name}: needs the tenant_isolation policy (USING = WITH CHECK on nullif(current_setting('app.tenant_id', true), '')).");
        }
    }

    private sealed record TableInfo(string Schema, string Name, string? TenantColumn, bool RlsEnabled, bool RlsForced, string? Using, string? WithCheck);

    private static async Task<List<TableInfo>> ReadTablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT n.nspname, c.relname,
                   (SELECT CASE WHEN a.attnotnull THEN 'uuid' ELSE 'uuid?' END FROM pg_attribute a
                     WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND a.atttypid = 'uuid'::regtype AND NOT a.attisdropped),
                   c.relrowsecurity, c.relforcerowsecurity, p.qual, p.with_check
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_policies p ON p.schemaname = n.nspname AND p.tablename = c.relname AND p.policyname = 'tenant_isolation'
            WHERE c.relkind IN ('r', 'p') AND c.relname <> '__ef_migrations_history' AND NOT (n.nspname = ANY(@system))
            ORDER BY 1, 2
            """, connection);
        command.Parameters.AddWithValue("system", SystemSchemas);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var tables = new List<TableInfo>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            tables.Add(new TableInfo(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3), reader.GetBoolean(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return tables;
    }
}
