using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Karry.Modules.Identity.Domain;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Identity.Tests.Api;

/// <summary>
/// The users import (US-P1-ORG-03, ADR-0027 §5) on the real pipeline with a real Keycloak: errors on their cells block
/// the file; office users are invited in two steps (Karry in the commit, then their Keycloak account and invitation from
/// the Identity outbox); field users are active without a PIN until an administrator sets one; privileged roles are
/// refused; the same file again changes nothing and a new role is added, never one removed; office seats are counted.
/// </summary>
[Trait("Story", "US-P1-ORG-03")]
public sealed class UserImportsTests(IdentityHost host) : IClassFixture<IdentityHost>
{
    private static readonly string[] Headers = ["Prénom", "Nom", "E-mail (bureau)", "Téléphone (bureau)", "Matricule (terrain)", "Rôle", "Site"];

    private readonly TenantMembers api = new(host);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Errors_block_the_file_then_office_users_are_invited_through_keycloak_and_field_users_wait_for_their_pin()
    {
        var (tenant, admin, imports) = await AdminAsync();
        var site = $"S{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        host.Sites.Add(site);
        var email = $"vente-{Guid.NewGuid():N}@iso.test";
        Row[] broken =
        [
            Office("Leila", email, Roles.Commercial),
            Office("Karim", $"k-{Guid.NewGuid():N}@iso.test", Roles.TenantAdmin),
            Office("Nour", "pas-un-email", Roles.Commercial),
            Field("Sami", "OP-1", Roles.Operator) with { Email = "sami@iso.test" },
            Field("Hedi", "OP-2", Roles.Commercial),
            Field("Ali", "OP-3", Roles.Operator) with { Site = "CAR-OUEST" },
            Office("Leila bis", email.ToUpperInvariant(), Roles.Commercial),
        ];

        var job = await imports.ImportAsync(tenant, "users", Workbook(broken), Ct);

        (await imports.IssuesAsync(tenant, job, "error", Ct)).Should().BeEquivalentTo([
            (2, "C2", "imports.key_duplicate"),
            (3, "F3", "identity.role_privileged"),
            (4, "C4", "identity.email_invalid"),
            (5, "E5", "identity.user_kind_ambiguous"),
            (6, "F6", "identity.role_needs_account"),
            (7, "G7", "identity.site_invalid"),
            (8, "C8", "imports.key_duplicate"),
        ]);
        (await imports.RequestCommitAsync(tenant, job, Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var phone = $"+2169{Random.Shared.Next(1000000, 9999999)}";
        var done = await imports.ImportAndCommitAsync(tenant, "users",
            Workbook(Office("Leila", email, Roles.Commercial), Office("Mehdi", null, Roles.MaintenancePlanner) with { Phone = phone }, Field("Sami", "OP-1", Roles.Operator) with { Site = site.ToLowerInvariant() }), Ct);

        done.GetProperty("created").GetInt32().Should().Be(3);
        (await ScalarAsync<long>($"SELECT count(*) FROM identity.users WHERE tenant_id = '{tenant}' AND kind = 'Office' AND status = 'Invited' AND subject IS NULL"))
            .Should().Be(2, "the Keycloak accounts come in the second step");
        (await ScalarAsync<string>($"SELECT status || '/' || coalesce(pin_hash, 'no pin') FROM identity.users WHERE tenant_id = '{tenant}' AND employee_id = 'OP-1'"))
            .Should().Be("Active/no pin");

        await host.RunIdentityEventsAsync();

        (await ScalarAsync<long>($"SELECT count(*) FROM identity.users WHERE tenant_id = '{tenant}' AND kind = 'Office' AND status = 'Invited' AND subject IS NOT NULL")).Should().Be(2);
        var accepted = await api.SendAsync(HttpMethod.Post, "/api/v1/identity/invitations/accept", null,
            new { token = host.Sender.TokenFor(email), password = "Correct-Horse-Battery-9" });
        accepted.StatusCode.Should().Be(HttpStatusCode.NoContent, "the delivered link is the reissued one, linked to the new account");
        host.Sender.Sent.Should().Contain(m => m.Destination == phone);
        await host.RunIdentityEventsAsync();
        host.Sender.Sent.Count(m => m.Destination == email).Should().Be(1, "a user is invited once, even when the events run again");

        var operatorId = await ScalarAsync<Guid>($"SELECT id FROM identity.users WHERE tenant_id = '{tenant}' AND employee_id = 'OP-1'");
        (await PinSetAsync(operatorId, admin)).Should().BeFalse("the portal shows who still needs a PIN");
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Put, $"/api/v1/identity/users/{operatorId}/pin", admin, new { pin = "111111" }),
            HttpStatusCode.BadRequest, "identity.pin_too_simple");
        (await api.SendAsync(HttpMethod.Put, $"/api/v1/identity/users/{operatorId}/pin", admin, new { pin = "583920" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await PinSetAsync(operatorId, admin)).Should().BeTrue();
        (await ScalarAsync<string>($"SELECT pin_hash FROM identity.users WHERE id = '{operatorId}'")).Should().NotBeNullOrEmpty().And.NotContain("583920");
        var ledger = await api.LedgerAsync(tenant);
        ledger.Should().ContainSingle(e => e.Type == "identity.user_pin_set.v1").Which.Payload.ToString().Should().NotContain("583920");
        ledger.Count(e => e.Type == "identity.invitation_reissued.v1").Should().Be(2);
    }

    [Fact]
    public async Task The_same_file_again_changes_nothing_a_new_role_is_added_and_a_deactivated_user_is_refused()
    {
        var (tenant, admin, imports) = await AdminAsync();
        Row[] rows = [Field("Sami", "OP-1", Roles.Operator), Field("Ali", "OP-2", Roles.Mechanic)];
        await imports.ImportAndCommitAsync(tenant, "users", Workbook(rows), Ct);

        var again = await imports.CommitAsync(tenant, await imports.ImportAsync(tenant, "users", Workbook(rows), Ct), Ct);
        (again.GetProperty("created").GetInt32(), again.GetProperty("unchanged").GetInt32()).Should().Be((0, 2), "TC-P1-ORG-002 on the employee ID");

        var added = await imports.ImportAndCommitAsync(tenant, "users", Workbook(rows[0] with { Role = Roles.Weighmaster }), Ct);
        added.GetProperty("updated").GetInt32().Should().Be(1);
        (await ScalarAsync<string>($"SELECT roles::text FROM identity.users WHERE tenant_id = '{tenant}' AND employee_id = 'OP-1'"))
            .Should().Contain(Roles.Operator).And.Contain(Roles.Weighmaster, "a role is added; the file never removes one");

        var ali = await ScalarAsync<Guid>($"SELECT id FROM identity.users WHERE tenant_id = '{tenant}' AND employee_id = 'OP-2'");
        (await api.SendAsync(HttpMethod.Post, $"/api/v1/identity/users/{ali}/deactivate", admin)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var refused = await imports.ImportAsync(tenant, "users", Workbook(rows), Ct);
        (await imports.IssuesAsync(tenant, refused, "error", Ct)).Should().ContainSingle().Which.Should().Be((3, "E3", "identity.user_deactivated"));
    }

    [Fact]
    public async Task Office_seats_are_counted_for_the_whole_file_and_field_users_are_not()
    {
        var (tenant, _, imports) = await AdminAsync();
        host.SeatLimits.ByTenant[tenant] = 2; // ceiling 3 seats (2 + 10 % rounded up); the administrator holds one

        var tooMany = await imports.ImportAsync(tenant, "users", Workbook(
            Office("A", $"a-{Guid.NewGuid():N}@iso.test", Roles.Commercial), Office("B", $"b-{Guid.NewGuid():N}@iso.test", Roles.Commercial),
            Office("C", $"c-{Guid.NewGuid():N}@iso.test", Roles.Commercial)), Ct);
        (await imports.IssuesAsync(tenant, tooMany, "error", Ct)).Should().ContainSingle().Which.Should().Be((0, null, "billing.limit_reached"));

        var fieldOnly = await imports.ImportAndCommitAsync(tenant, "users",
            Workbook([.. Enumerable.Range(1, 5).Select(i => Field($"Op{i}", $"OP-{i}", Roles.Operator))]), Ct);
        fieldOnly.GetProperty("created").GetInt32().Should().Be(5, "field users are unlimited on every plan");
    }

    private static Row Office(string first, string? email, string role) => new(first, "Import", email, null, null, role, null);

    private static Row Field(string first, string employeeId, string role) => new(first, "Import", null, null, employeeId, role, null);

    private sealed record Row(string? FirstName, string? LastName, string? Email, string? Phone, string? EmployeeId, string? Role, string? Site)
    {
        public string?[] Cells => [FirstName, LastName, Email, Phone, EmployeeId, Role, Site];
    }

    private static byte[] Workbook(params Row[] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Données");
        for (var c = 0; c < Headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = Headers[c];
        }

        for (var r = 0; r < rows.Length; r++)
        {
            var cells = rows[r].Cells;
            for (var c = 0; c < cells.Length; c++)
            {
                if (cells[c] is { } value)
                {
                    sheet.Cell(r + 2, c + 1).Value = value;
                }
            }
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    /// <summary>A fresh tenant, its Tenant Admin's token, and an import driver acting as them.</summary>
    private async Task<(Guid Tenant, string Token, ImportDriver Imports)> AdminAsync()
    {
        var tenant = await api.TenantAsync();
        var subject = $"admin-{Guid.NewGuid():N}";
        await api.InsertAsync(tenant, "Office", [(Roles.TenantAdmin, null)], subject, employeeId: null);
        return (tenant, TestTokens.ForTenant(tenant, subject), new ImportDriver(host.Client, host.RunImportsAsync, subject));
    }

    private async Task<bool> PinSetAsync(Guid user, string token)
    {
        using var response = await api.SendAsync(HttpMethod.Get, $"/api/v1/identity/users/{user}", token);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pinSet").GetBoolean();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
