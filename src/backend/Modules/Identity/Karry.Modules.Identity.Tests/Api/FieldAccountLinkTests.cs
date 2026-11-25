using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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

    [Fact]
    [Trait("Story", "US-P2-VER-01")]
    public async Task A_reviewer_learns_whether_their_reviews_know_their_field_account_and_whether_an_unlinked_one_has_their_name()
    {
        var tenant = await api.TenantAsync();
        var admin = await api.MemberAsync(tenant, host.Clock.GetUtcNow(), (Roles.TenantAdmin, null));
        var reviewer = await api.MemberAsync(tenant, (Roles.Controller, null));
        var me = await IdOfAsync(reviewer);

        var alone = await StatusAsync(reviewer);
        var field = await api.FieldUserAsync(tenant, (Roles.Operator, null));
        await RenameAsync(field, "SEEDED", "member");
        var namesake = await StatusAsync(reviewer);
        await RenameAsync(field, "Seeded", "Mem%");
        var pattern = await StatusAsync(reviewer);
        await RenameAsync(field, "Seeded", "Member");
        (await api.SendAsync(HttpMethod.Put, $"/api/v1/identity/users/{me}/field-account", admin, new { fieldUserId = field })).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        var linked = await StatusAsync(reviewer);

        alone.Should().Be((false, false), "no field account has the name");
        namesake.Should().Be((false, true), "the same name, whatever the case");
        pattern.Should().Be((false, false), "a name is matched as it is, never as a pattern");
        linked.Should().Be((true, false));
    }

    private async Task<(bool Linked, bool Namesake)> StatusAsync(string token)
    {
        using var response = await api.SendAsync(HttpMethod.Get, "/api/v1/identity/me/field-account", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (body.GetProperty("linked").GetBoolean(), body.GetProperty("unlinkedNamesake").GetBoolean());
    }

    private async Task<Guid> IdOfAsync(string token)
    {
        using var response = await api.SendAsync(HttpMethod.Get, "/api/v1/identity/me", token);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task RenameAsync(Guid user, string first, string last)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("UPDATE identity.users SET first_name = $1, last_name = $2 WHERE id = $3", connection);
        command.Parameters.AddWithValue(first);
        command.Parameters.AddWithValue(last);
        command.Parameters.AddWithValue(user);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<Guid?> FieldAccountOfAsync(Guid tenant, string subject)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        return await scope.ServiceProvider.GetRequiredService<IReviewers>().FieldAccountOfAsync(subject, Ct);
    }
}
