using System.Reflection;
using System.Runtime.ExceptionServices;
using Karry.Testing;
using Npgsql;

namespace Karry.Host.IntegrationTests;

/// <summary>
/// A new environment as the deployment brings it up (P1.17): <c>Karry.Host migrate</c> as the schema owner, then
/// <c>Karry.Host seed-demo</c> for the demo tenants, run again on every deployment. The real entry point runs against a
/// database prepared like an environment (roles and extensions); nothing is stubbed.
/// </summary>
[Trait("Story", "US-P1-TEN-01")]
public sealed class FreshDeploymentTests : IAsyncLifetime
{
    private PostgresServer? server;
    private KarryDatabase database = null!;

    public async ValueTask InitializeAsync()
    {
        server = await PostgresServer.StartAsync();
        database = await server.NewKarryDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Migrate_then_seed_twice_gives_the_same_demo_and_keeps_a_change_made_in_the_portal()
    {
        (await RunAsync("migrate", "Testing")).Should().Be(0);
        (await RunAsync("migrate", "Testing")).Should().Be(0, "migrating an up-to-date database changes nothing");

        (await RunAsync("seed-demo", "Testing")).Should().Be(0);
        var first = await DemoAsync();
        await ExecuteAsync("UPDATE tenancy.tenants SET legal_name = 'Carrière A (renamed in the portal)', settings_changed_at = now() WHERE slug = 'demo-carriere-a'");
        (await RunAsync("seed-demo", "Testing")).Should().Be(0, "the seed runs on every deployment");
        var second = await DemoAsync();

        first.Should().Be(new Demo(Tenants: 2, Sites: 3, Materials: 6, Machines: 11, Subscriptions: 2, Checklists: first.Checklists));
        first.Checklists.Should().BePositive("each demo company gets its pre-start checklists published");
        second.Should().Be(first, "a second run creates nothing twice");
        (await ScalarAsync("SELECT legal_name FROM tenancy.tenants WHERE slug = 'demo-carriere-a'")).Should().Be("Carrière A (renamed in the portal)");
        (await ScalarAsync("SELECT legal_name || '|' || currency || '|' || time_zone FROM tenancy.tenants WHERE slug = 'demo-carriere-b'"))
            .Should().Be("Carrière Démo B SA|XOF|Africa/Conakry", "a company still on the defaults gets the demo settings");
        (await ScalarAsync("SELECT count(*) FROM identity.users")).Should().Be(0L, "no Keycloak is configured, so no demo user is created");
    }

    [Fact]
    public async Task Migrate_without_the_owner_connection_string_exits_2()
    {
        (await RunAsync("migrate", "Testing", owner: string.Empty)).Should().Be(2, "the runtime role cannot change the schema");
    }

    [Fact]
    public async Task Seed_demo_is_refused_in_production_and_changes_nothing()
    {
        (await RunAsync("migrate", "Testing")).Should().Be(0);

        (await RunAsync("seed-demo", "Production")).Should().Be(2);
        (await ScalarAsync("SELECT count(*) FROM tenancy.tenants")).Should().Be(0L);
    }

    private sealed record Demo(long Tenants, long Sites, long Materials, long Machines, long Subscriptions, long Checklists);

    private async Task<Demo> DemoAsync() => new(
        (long)(await ScalarAsync("SELECT count(*) FROM tenancy.tenants WHERE slug LIKE 'demo-carriere-%'"))!,
        (long)(await ScalarAsync("SELECT count(*) FROM organization.sites"))!,
        (long)(await ScalarAsync("SELECT count(*) FROM materials.materials"))!,
        (long)(await ScalarAsync("SELECT count(*) FROM assets.assets"))!,
        (long)(await ScalarAsync("SELECT count(*) FROM billing.subscriptions"))!,
        (long)(await ScalarAsync("SELECT count(*) FROM fieldops.checklist_templates"))!);

    /// <summary>The real entry point (<c>Program</c>) with the command, the environment and the database's two roles.</summary>
    private Task<int> RunAsync(string command, string environment, string? owner = null)
    {
        var entry = typeof(Program).Assembly.EntryPoint!;
        string[] args =
        [
            command, "--environment", environment,
            $"--ConnectionStrings:Karry={database.App}",
            $"--ConnectionStrings:KarryOwner={owner ?? database.Owner}",
        ];

        // The compiler's entry point of an async Main blocks on it: run it off the test thread.
        return Task.Run(() =>
        {
            try
            {
                return entry.Invoke(null, [args]) switch
                {
                    int code => code,
                    Task<int> task => task.GetAwaiter().GetResult(),
                    var other => throw new InvalidOperationException($"Unexpected entry point result {other}."),
                };
            }
            catch (TargetInvocationException invocation) when (invocation.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(invocation.InnerException);
                throw;
            }
        });
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.Admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.Admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
