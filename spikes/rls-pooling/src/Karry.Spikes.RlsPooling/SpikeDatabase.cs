using System.Reflection;
using Npgsql;

namespace Karry.Spikes.RlsPooling;

/// <summary>
/// Creates an isolated throwaway database with an owner role and a restricted app role.
/// Requires a superuser connection string (local dev or CI service container only).
/// </summary>
public sealed class SpikeDatabase
{
    public const string OwnerRole = "karry_spike_owner";
    public const string AppRole = "karry_spike_app";
    private const string RolePassword = "spike-only-not-a-secret";

    private SpikeDatabase(string adminConnectionString, string databaseName)
    {
        AdminConnectionString = adminConnectionString;
        DatabaseName = databaseName;
    }

    public string AdminConnectionString { get; }
    public string DatabaseName { get; }

    public string ConnectionStringFor(string role, Action<NpgsqlConnectionStringBuilder>? configure = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Database = DatabaseName,
            Username = role,
            Password = RolePassword,
        };
        configure?.Invoke(builder);
        return builder.ConnectionString;
    }

    public string AdminDatabaseConnectionString =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = DatabaseName }.ConnectionString;

    public static async Task<SpikeDatabase> CreateAsync(string adminConnectionString, CancellationToken cancellationToken)
    {
        var databaseName = $"karry_spike_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await ExecuteAsync(admin, $"""
                DO $$
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{OwnerRole}') THEN
                    CREATE ROLE {OwnerRole} LOGIN PASSWORD '{RolePassword}' NOSUPERUSER NOBYPASSRLS;
                  END IF;
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppRole}') THEN
                    CREATE ROLE {AppRole} LOGIN PASSWORD '{RolePassword}' NOSUPERUSER NOBYPASSRLS;
                  END IF;
                END $$;
                """, cancellationToken);
            await ExecuteAsync(admin, $"CREATE DATABASE {databaseName} OWNER {OwnerRole}", cancellationToken);
        }

        var database = new SpikeDatabase(adminConnectionString, databaseName);
        await using (var owner = new NpgsqlConnection(database.ConnectionStringFor(OwnerRole)))
        {
            await owner.OpenAsync(cancellationToken);
            await ExecuteAsync(owner, ReadSchema(), cancellationToken);
        }

        return database;
    }

    public async Task DropAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(AdminConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)", CancellationToken.None);
    }

    private static string ReadSchema()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("schema.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
