using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Identity.Tests.Api;

/// <summary>
/// Who may sign on a site's tablets (threat R-13, RULE-FO-09): the tablet's own unlock rule applied to one signer, on
/// PostgreSQL with row-level security.
/// </summary>
[Trait("Story", "US-P2-INT-01")]
public sealed class FieldSignersTests(IdentityHost host) : IClassFixture<IdentityHost>
{
    private static readonly Guid Site = Guid.Parse("01970000-0000-7000-8000-000000000c01");
    private static readonly Guid OtherSite = Guid.Parse("01970000-0000-7000-8000-000000000c02");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_active_field_user_of_the_site_or_of_the_company_may_sign_and_anyone_else_is_refused_with_the_reason()
    {
        var members = new TenantMembers(host);
        var (tenant, other) = (await members.TenantAsync(), await members.TenantAsync());
        var onSite = await members.FieldUserAsync(tenant, ("operator", Site));
        var companyWide = await members.FieldUserAsync(tenant, ("operator", null));
        var elsewhere = await members.FieldUserAsync(tenant, ("operator", OtherSite));
        var office = await members.InsertAsync(tenant, "Office", [("operator", Site)], $"signers-{Guid.NewGuid():N}", employeeId: null);
        var noEmployeeId = await members.InsertAsync(tenant, "Field", [("operator", Site)], subject: null, employeeId: null);
        var invited = await members.FieldUserAsync(tenant, ("operator", Site));
        var deactivated = await members.FieldUserAsync(tenant, ("operator", Site));
        var deactivatedAt = new DateTimeOffset(2026, 11, 20, 9, 30, 0, TimeSpan.Zero);
        await ExecuteAsync($"UPDATE identity.users SET status = 'Invited' WHERE id = '{invited}'");
        await ExecuteAsync($"UPDATE identity.users SET status = 'Deactivated', deactivated_at = '{deactivatedAt:O}' WHERE id = '{deactivated}'");
        var theirs = await members.FieldUserAsync(other, ("operator", Site));

        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        var signers = scope.ServiceProvider.GetRequiredService<IFieldSigners>();
        async Task<FieldSignerRefusal?> Refusal(Guid user) => await signers.RefusalAsync(user, Site, Ct);

        (await Refusal(onSite)).Should().BeNull();
        (await Refusal(companyWide)).Should().BeNull();
        (await Refusal(elsewhere)).Should().Be(new FieldSignerRefusal(FieldSignerRefusals.NotOnSite));
        (await Refusal(office)).Should().Be(new FieldSignerRefusal(FieldSignerRefusals.NotFieldUser));
        (await Refusal(noEmployeeId)).Should().Be(new FieldSignerRefusal(FieldSignerRefusals.NotFieldUser));
        (await Refusal(invited)).Should().Be(new FieldSignerRefusal(FieldSignerRefusals.Inactive));
        (await Refusal(deactivated)).Should().Be(new FieldSignerRefusal(FieldSignerRefusals.Deactivated, deactivatedAt));
        (await Refusal(theirs)).Should().Be(new FieldSignerRefusal(FieldSignerRefusals.Unknown), "another company's people are not this company's");
        (await Refusal(Guid.CreateVersion7())).Should().Be(new FieldSignerRefusal(FieldSignerRefusals.Unknown));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
