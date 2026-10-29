using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Identity.Tests.Api;

/// <summary>The names the audit viewer shows for the people in the trail (ADR-0029 §2), on PostgreSQL with row-level security.</summary>
[Trait("Story", "US-P1-AUD-04")]
public sealed class UserNamesTests(IdentityHost host) : IClassFixture<IdentityHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Names_come_back_by_subject_and_only_for_people_of_the_current_company()
    {
        var members = new TenantMembers(host);
        var tenant = await members.TenantAsync();
        var other = await members.TenantAsync();
        var mine = $"names-{Guid.NewGuid():N}";
        var theirs = $"names-{Guid.NewGuid():N}";
        await members.InsertAsync(tenant, "Office", [("operator", null)], mine, employeeId: null);
        await members.InsertAsync(tenant, "Office", [("operator", null)], $"names-{Guid.NewGuid():N}", employeeId: null); // not asked for
        await members.InsertAsync(tenant, "Field", [("operator", null)], subject: null, employeeId: $"E-{Guid.NewGuid():N}"[..20]);
        await members.InsertAsync(other, "Office", [("operator", null)], theirs, employeeId: null);

        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        var names = await scope.ServiceProvider.GetRequiredService<IUserNames>().BySubjectAsync([mine, theirs, "nobody"], Ct);

        names.Should().BeEquivalentTo(new Dictionary<string, string> { [mine] = "Seeded Member" },
            "another company's people stay hidden and unknown subjects are left out");
    }

    [Fact]
    public async Task No_subjects_ask_nothing()
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(await new TenantMembers(host).TenantAsync()));

        (await scope.ServiceProvider.GetRequiredService<IUserNames>().BySubjectAsync([], Ct)).Should().BeEmpty();
    }
}
