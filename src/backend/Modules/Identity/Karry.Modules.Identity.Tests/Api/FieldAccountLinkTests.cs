using System.Net;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Identity.Tests.Api;

/// <summary>
/// An office account linked to the same person's field account (ADR-0037 §4): separation of duties then sees a
/// controller who also drives as one reviewer. Set by a user administrator with a recent sign-in, ledgered, one office
/// account per field account.
/// </summary>
[Trait("Story", "US-P2-VER-02")]
public sealed class FieldAccountLinkTests(IdentityHost host) : IClassFixture<IdentityHost>
{
    private readonly TenantMembers api = new(host);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_administrator_links_and_unlinks_a_controllers_field_account_and_each_change_is_ledgered()
    {
        var tenant = await api.TenantAsync();
        var admin = await api.MemberAsync(tenant, host.Clock.GetUtcNow(), (Roles.TenantAdmin, null));
        var subject = $"ctl-{Guid.NewGuid():N}";
        var controller = await api.InsertAsync(tenant, "Office", [(Roles.Controller, null)], subject, employeeId: null);
        var field = await api.FieldUserAsync(tenant, (Roles.Operator, null));

        (await api.SendAsync(HttpMethod.Put, $"/api/v1/identity/users/{controller}/field-account", admin, new { fieldUserId = field })).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await FieldAccountOfAsync(tenant, subject)).Should().Be(field);
        (await api.SendAsync(HttpMethod.Put, $"/api/v1/identity/users/{controller}/field-account", admin, new { fieldUserId = field })).StatusCode
            .Should().Be(HttpStatusCode.NoContent, "the same link again changes nothing");

        (await api.SendAsync(HttpMethod.Put, $"/api/v1/identity/users/{controller}/field-account", admin, new { fieldUserId = (Guid?)null })).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await FieldAccountOfAsync(tenant, subject)).Should().BeNull();
        (await api.LedgerAsync(tenant)).Where(e => e.Type == "identity.user_field_account_linked.v1").Should().HaveCount(2, "linked, then unlinked; the repeat added nothing");
    }

    [Fact]
    public async Task A_field_account_links_to_one_office_account_only_to_a_field_account_and_only_after_a_recent_sign_in()
    {
        var tenant = await api.TenantAsync();
        var admin = await api.MemberAsync(tenant, host.Clock.GetUtcNow(), (Roles.TenantAdmin, null));
        var staleAdmin = await api.MemberAsync(tenant, host.Clock.GetUtcNow().AddHours(-2), (Roles.TenantAdmin, null));
        var first = await api.InsertAsync(tenant, "Office", [(Roles.Controller, null)], $"ctl-{Guid.NewGuid():N}", employeeId: null);
        var second = await api.InsertAsync(tenant, "Office", [(Roles.Controller, null)], $"ctl-{Guid.NewGuid():N}", employeeId: null);
        var field = await api.FieldUserAsync(tenant, (Roles.Operator, null));
        var unlinked = await api.FieldUserAsync(tenant, (Roles.Operator, null));
        (await api.SendAsync(HttpMethod.Put, $"/api/v1/identity/users/{first}/field-account", admin, new { fieldUserId = field })).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Put, $"/api/v1/identity/users/{second}/field-account", admin, new { fieldUserId = field }),
            HttpStatusCode.Conflict, "identity.field_account_linked");
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Put, $"/api/v1/identity/users/{second}/field-account", admin, new { fieldUserId = first }),
            HttpStatusCode.BadRequest, "identity.not_field_user");
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Put, $"/api/v1/identity/users/{field}/field-account", admin, new { fieldUserId = unlinked }),
            HttpStatusCode.UnprocessableEntity, "identity.not_office_user");
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Put, $"/api/v1/identity/users/{second}/field-account", admin, new { fieldUserId = Guid.CreateVersion7() }),
            HttpStatusCode.BadRequest, "identity.field_user_not_found");
        (await api.SendAsync(HttpMethod.Put, $"/api/v1/identity/users/{second}/field-account", staleAdmin,
            new { fieldUserId = (Guid?)null })).StatusCode.Should().NotBe(HttpStatusCode.NoContent, "the link decides what a controller may approve");
    }

    private async Task<Guid?> FieldAccountOfAsync(Guid tenant, string subject)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        return await scope.ServiceProvider.GetRequiredService<IReviewers>().FieldAccountOfAsync(subject, Ct);
    }
}
