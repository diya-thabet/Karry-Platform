using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Outbox;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;
using Karry.Modules.Identity.Tests.Api;
using Karry.Modules.Tenancy.Infrastructure;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Identity.Tests.Application;

/// <summary>
/// The demo users of <c>seed-demo</c> (US-P1-PLT-02 AC2) against the real Keycloak and database: one user per default
/// role, office ones with an enabled Keycloak account, field ones with their demo employee ID; a second run creates none.
/// </summary>
[Trait("Story", "US-P1-PLT-02")]
public sealed class DemoUsersSeederTests(IdentityHost host) : IClassFixture<IdentityHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seeding_twice_gives_one_active_user_per_default_role_and_creates_nothing_the_second_time()
    {
        var members = new TenantMembers(host);
        var (tenant, slug) = await members.CompanyAsync();
        await host.Services.GetRequiredService<OutboxDispatcher<TenancyDbContext>>().DispatchPendingAsync(100, Ct); // the tenant in Keycloak

        var first = await SeedAsync(tenant);
        var second = await SeedAsync(tenant);

        first.Should().Be(Roles.Defaults.Count);
        second.Should().Be(0, "the seed runs on every deployment");
        var users = await UsersAsync(tenant);
        users.Should().HaveCount(Roles.Defaults.Count).And.OnlyContain(u => u.Status == "Active");
        foreach (var role in Roles.Defaults.Values)
        {
            var label = role.Office ? $"{role.Code}@{slug}.karry.test" : $"DEMO-{role.Code.ToUpperInvariant()}";
            users.Should().ContainSingle(u => u.Login == label, $"role {role.Code} gets its demo login");
        }

        users.Where(u => u.Kind == "Office").Should().OnlyContain(u => u.Subject != null, "an office user is linked to their Keycloak account");
        var admin = Roles.Defaults.Values.First(r => r.Office);
        using var account = await host.Keycloak.AdminAsync(HttpMethod.Get, $"users?username={Uri.EscapeDataString($"{admin.Code}@{slug}.karry.test")}&exact=true", cancellationToken: Ct);
        (await account.Content.ReadFromJsonAsync<JsonElement>(Ct))[0].GetProperty("enabled").GetBoolean().Should().BeTrue("the demo password is set, so the account is usable");
    }

    [Fact]
    public async Task A_tenant_missing_from_the_catalogue_is_refused()
    {
        var seed = () => SeedAsync(Guid.CreateVersion7());

        await seed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not in the catalog*");
    }

    private async Task<int> SeedAsync(Guid tenant)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DemoUsersSeeder>().SeedAsync(new TenantId(tenant), "karry-demo-test-only", Ct);
    }

    private sealed record User(string Kind, string Status, string Login, string? Subject);

    private async Task<List<User>> UsersAsync(Guid tenant)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT kind, status, coalesce(email, employee_id), subject FROM identity.users WHERE tenant_id = $1", connection);
        command.Parameters.AddWithValue(tenant);
        var users = new List<User>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            users.Add(new User(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return users;
    }
}
