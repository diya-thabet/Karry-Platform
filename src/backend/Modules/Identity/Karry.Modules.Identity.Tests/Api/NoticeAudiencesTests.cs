using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Identity.Tests.Api;

/// <summary>
/// What Identity tells Notifications (ADR-0039 §2, §4): who holds a role on a site, where to e-mail an active person
/// (never a person who left or has no address), and where a company's portal is for the links of an e-mail.
/// </summary>
[Trait("Story", "US-P2-NOT-01")]
public sealed class NoticeAudiencesTests(IdentityHost host) : IClassFixture<IdentityHost>
{
    private readonly TenantMembers api = new(host);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_active_person_with_an_address_is_e_mailed_there_and_no_one_else_is()
    {
        var (tenant, slug) = await api.CompanyAsync();
        var site = Guid.CreateVersion7();
        var manager = await api.InsertAsync(tenant, "Office", [(Roles.SiteManager, site)], $"mgr-{Guid.NewGuid():N}", employeeId: null);
        var gone = await api.InsertAsync(tenant, "Office", [(Roles.SiteManager, site)], $"old-{Guid.NewGuid():N}", employeeId: null);
        var operatorOnly = await api.FieldUserAsync(tenant, (Roles.Operator, site));
        await ExecuteAsync($"UPDATE identity.users SET email = 'awa.camara@{slug}.test', first_name = 'Awa', last_name = 'Camara' WHERE id = '{manager}'");
        await ExecuteAsync($"UPDATE identity.users SET email = 'left@{slug}.test', status = 'Deactivated' WHERE id = '{gone}'");

        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        var audiences = scope.ServiceProvider.GetRequiredService<INoticeAudiences>();

        (await audiences.ContactOfAsync(manager, Ct)).Should().Be(new NoticeContact($"awa.camara@{slug}.test", "Awa Camara"));
        (await audiences.ContactOfAsync(gone, Ct)).Should().BeNull("a person who left is not mailed");
        (await audiences.ContactOfAsync(operatorOnly, Ct)).Should().BeNull("a phone-only operator has no address");
        (await audiences.ContactOfAsync(Guid.CreateVersion7(), Ct)).Should().BeNull();
        (await audiences.HoldersAsync([Roles.SiteManager], site, Ct)).Should().Equal(manager);
    }

    [Fact]
    public void The_portal_of_a_company_is_its_slug_in_the_configured_address()
    {
        var addresses = host.Services.GetRequiredService<IAppAddresses>();

        addresses.PortalFor("acme").Host.Should().StartWith("acme.");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
