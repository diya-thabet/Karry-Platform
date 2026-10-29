using System.Net;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Domain;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Identity.Tests.Api;

/// <summary>
/// Support access on the real pipeline (ADR-0029 §4, US-P1-OPS-03 AC3): a Tenant Admin's support code, a Karry
/// operator's session with it or by break-glass, one hour, ended early by either side, everything in the company's
/// ledger; in a session, the operator reads with the built-in support access (reads only, costs masked) and every
/// request is in the ledger.
/// </summary>
[Trait("Story", "US-P1-OPS-03")]
[Trait("TestCase", "TC-P1-OPS-004")]
public sealed class SupportApiTests(IdentityHost host) : IClassFixture<IdentityHost>
{
    private const string Consents = "/api/v1/identity/support/consents";
    private const string Sessions = "/api/v1/identity/support/sessions";

    private readonly TenantMembers api = new(host);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string OperatorSessions(Guid tenant) => $"/api/v1/identity/tenants/{tenant}/support-sessions";

    [Fact]
    public async Task An_operator_opens_one_hour_of_support_with_the_company_s_code_once_and_it_is_ledgered()
    {
        var company = await CompanyAsync();
        var code = await CodeAsync(company);
        var operatorToken = TestTokens.ForPlatformOperator("op-anna", ("name", "Anna Support"));

        // Read out loud over the phone: case, spaces and O for 0 do not matter.
        var typed = code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal).Replace('0', 'o');
        var session = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Post, OperatorSessions(company.Id), operatorToken, new { code = typed }), HttpStatusCode.Created);
        session.GetProperty("mode").GetString().Should().Be("consent");
        session.GetProperty("tenantId").GetGuid().Should().Be(company.Id);
        session.GetProperty("operatorName").GetString().Should().Be("Anna Support");
        session.GetProperty("endsAt").GetDateTimeOffset().Should().Be(host.Clock.GetUtcNow().AddHours(1));
        session.GetProperty("active").GetBoolean().Should().BeTrue();

        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, OperatorSessions(company.Id), operatorToken, new { code }), HttpStatusCode.UnprocessableEntity, "support.consent_invalid");
        var ledger = await api.LedgerAsync(company.Id);
        ledger.Select(e => e.Type).Should().ContainInOrder("identity.support_consent_created.v1", "identity.support_session_started.v1");
        var started = ledger.Single(e => e.Type == "identity.support_session_started.v1").Payload;
        started.GetProperty("operator").GetString().Should().Be("op-anna");
        started.GetProperty("mode").GetString().Should().Be("consent");
        ledger.Select(e => e.Payload.ToString()).Should().NotContain(p => p.Contains(code, StringComparison.Ordinal) || p.Contains(code.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal));
        host.SupportNotices.Sent.Should().NotContain(n => n.CompanyName.Contains(company.Slug, StringComparison.Ordinal), "a consent is not an emergency");
    }

    [Fact]
    public async Task Without_a_valid_code_of_this_company_or_a_reason_there_is_no_session()
    {
        var company = await CompanyAsync();
        var other = await CompanyAsync();
        var operatorToken = TestTokens.ForPlatformOperator();
        var code = await CodeAsync(company);

        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, OperatorSessions(other.Id), operatorToken, new { code }), HttpStatusCode.UnprocessableEntity, "support.consent_invalid");
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, OperatorSessions(company.Id), operatorToken, new { code = "0000-0000-0000-0000" }), HttpStatusCode.UnprocessableEntity, "support.consent_invalid");
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, OperatorSessions(company.Id), operatorToken, new { code = "not a code" }), HttpStatusCode.UnprocessableEntity, "support.consent_invalid");
        (await api.SendAsync(HttpMethod.Post, OperatorSessions(company.Id), operatorToken, new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await api.SendAsync(HttpMethod.Post, OperatorSessions(company.Id), operatorToken, new { code, reason = new string('r', 40) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, OperatorSessions(company.Id), operatorToken, new { reason = "Customer unreachable" }), HttpStatusCode.BadRequest, "support.reason_required");
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, OperatorSessions(Guid.CreateVersion7()), operatorToken, new { code }), HttpStatusCode.NotFound, "support.tenant_not_found");

        host.Clock.Advance(TimeSpan.FromMinutes(30));
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, OperatorSessions(company.Id), operatorToken, new { code }), HttpStatusCode.UnprocessableEntity, "support.consent_invalid");
        (await api.LedgerAsync(company.Id)).Select(e => e.Type).Should().NotContain("identity.support_session_started.v1");
    }

    [Fact]
    public async Task Break_glass_opens_with_a_written_reason_alerts_and_tells_the_tenant_admins()
    {
        var company = await CompanyAsync();
        await EmailAsync(company.Id, "admin@quarry.test");
        var operatorToken = TestTokens.ForPlatformOperator("op-sami", ("given_name", "Sami"), ("family_name", "Ops"));
        const string reason = "INC-42: weighbridge totals wrong, customer unreachable at night";

        var session = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Post, OperatorSessions(company.Id), operatorToken, new { reason }), HttpStatusCode.Created);

        session.GetProperty("mode").GetString().Should().Be("break_glass");
        session.GetProperty("reason").GetString().Should().Be(reason);
        session.GetProperty("operatorName").GetString().Should().Be("Sami Ops");
        var notice = host.SupportNotices.Sent.Should().ContainSingle(n => n.Reason == reason).Subject;
        notice.Recipients.Should().Equal(("Seeded Member", "admin@quarry.test"));
        notice.CompanyName.Should().Be($"Carrière {company.Slug}");
        notice.OperatorName.Should().Be("Sami Ops");
        notice.EndsAt.Should().Be(notice.StartedAt.AddHours(1));
        var started = (await api.LedgerAsync(company.Id)).Single(e => e.Type == "identity.support_session_started.v1").Payload;
        started.GetProperty("mode").GetString().Should().Be("break_glass");
        started.GetProperty("reason").GetString().Should().Be(reason);
    }

    [Fact]
    public async Task The_company_sees_its_sessions_and_ends_one_and_the_operator_ends_only_their_own()
    {
        var company = await CompanyAsync();
        var anna = TestTokens.ForPlatformOperator("op-anna");
        var omar = TestTokens.ForPlatformOperator("op-omar");
        var first = await StartAsync(company, anna);
        var second = await StartAsync(company, anna);

        var list = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Get, Sessions, company.Admin), HttpStatusCode.OK);
        list.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).Should().BeEquivalentTo([first, second]);
        list.GetProperty("items")[0].TryGetProperty("operator", out _).Should().BeFalse("the company sees the operator's name, not their account");

        var ended = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Post, $"{Sessions}/{first}/end", company.Admin), HttpStatusCode.OK);
        ended.GetProperty("active").GetBoolean().Should().BeFalse();
        ended.GetProperty("endedAt").GetDateTimeOffset().Should().Be(host.Clock.GetUtcNow());
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, $"{OperatorSessions(company.Id)}/{first}/end", anna), HttpStatusCode.UnprocessableEntity, "support.session_ended");

        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, $"{OperatorSessions(company.Id)}/{second}/end", omar), HttpStatusCode.NotFound, "support.session_not_found");
        (await api.SendAsync(HttpMethod.Post, $"{OperatorSessions(company.Id)}/{second}/end", anna)).StatusCode.Should().Be(HttpStatusCode.OK);

        var ends = (await api.LedgerAsync(company.Id)).Where(e => e.Type == "identity.support_session_ended.v1").Select(e => e.Payload).ToList();
        ends.Select(e => e.GetProperty("byOperator").GetBoolean()).Should().Equal(false, true);
        var operatorList = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Get, OperatorSessions(company.Id), omar), HttpStatusCode.OK);
        operatorList.GetProperty("items").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task A_session_stops_being_active_after_its_hour_and_cannot_be_ended_then()
    {
        var company = await CompanyAsync();
        var anna = TestTokens.ForPlatformOperator("op-anna");
        var id = await StartAsync(company, anna);

        host.Clock.Advance(TimeSpan.FromHours(1));

        var list = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Get, Sessions, company.Admin), HttpStatusCode.OK);
        list.GetProperty("items").EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id).GetProperty("active").GetBoolean().Should().BeFalse();
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, $"{Sessions}/{id}/end", company.Admin), HttpStatusCode.UnprocessableEntity, "support.session_ended");
    }

    [Fact]
    public async Task Only_a_tenant_admin_who_signed_in_recently_creates_codes_and_only_operators_open_sessions()
    {
        var company = await CompanyAsync();
        var executive = await api.MemberAsync(company.Id, (Roles.Executive, null));
        var stale = await api.MemberAsync(company.Id, host.Clock.GetUtcNow().AddHours(-2), (Roles.TenantAdmin, null));

        await TenantMembers.ExpectForbiddenAsync(await api.SendAsync(HttpMethod.Post, Consents, executive));
        await TenantMembers.ExpectForbiddenAsync(await api.SendAsync(HttpMethod.Get, Sessions, executive));
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, Consents, stale), HttpStatusCode.Unauthorized, "auth.step_up_required");
        await TenantMembers.ExpectForbiddenAsync(await api.SendAsync(HttpMethod.Post, OperatorSessions(company.Id), company.Admin, new { reason = new string('r', 40) }));
        await TenantMembers.ExpectForbiddenAsync(await api.SendAsync(HttpMethod.Get, OperatorSessions(company.Id), company.Admin));
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, $"{Sessions}/{Guid.CreateVersion7()}/end", company.Admin), HttpStatusCode.NotFound, "support.session_not_found");
    }

    [Fact]
    public async Task In_a_session_the_operator_reads_with_costs_masked_cannot_write_and_every_request_is_ledgered()
    {
        var company = await CompanyAsync();
        var anna = TestTokens.ForPlatformOperator("op-anna");
        var session = await StartAsync(company, anna);
        var header = Header(company.Id, session);

        var quote = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Get, "/api/v1/probe/quote", anna, headers: header), HttpStatusCode.OK);
        quote.TryGetProperty("unitPrice", out _).Should().BeFalse("support sees costs masked, whatever the company's roles see");
        quote.GetProperty("maskedFields").EnumerateArray().Select(f => f.GetString()).Should().Equal("unitPrice");
        (await api.SendAsync(HttpMethod.Get, "/api/v1/identity/users", anna, headers: header)).StatusCode.Should().Be(HttpStatusCode.OK);
        await TenantMembers.ExpectForbiddenAsync(await api.SendAsync(HttpMethod.Get, "/api/v1/identity/devices", anna, headers: header));
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, Consents, anna, headers: header), HttpStatusCode.Forbidden, "support.read_only");
        await TenantMembers.ExpectProblemAsync(
            await api.SendAsync(HttpMethod.Post, "/api/v1/identity/invitations", anna, new { firstName = "X", lastName = "Y", email = "x@y.test", roles = Array.Empty<object>() }, header),
            HttpStatusCode.Forbidden, "support.read_only");
        var someone = Guid.CreateVersion7();
        (await api.SendAsync(HttpMethod.Get, $"/api/v1/identity/users/{someone}", anna, headers: header)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await api.SendAsync(HttpMethod.Get, Sessions, company.Admin)).StatusCode.Should().Be(HttpStatusCode.OK, "the company's own calls are not support requests");

        var requests = (await api.LedgerAsync(company.Id)).Where(e => e.Type == "identity.support_request.v1").Select(e => e.Payload).ToList();
        requests.Select(r => (r.GetProperty("method").GetString(), r.GetProperty("path").GetString(), r.GetProperty("status").GetInt32()))
            .Should().Equal(
                ("GET", "/api/v1/probe/quote", 200),
                ("GET", "/api/v1/identity/users", 200),
                ("GET", "/api/v1/identity/devices", 403),
                ("POST", "/api/v1/identity/support/consents", 403),
                ("POST", "/api/v1/identity/invitations", 403),
                ("GET", "/api/v1/identity/users/{id:guid}", 404));
        requests.Should().OnlyContain(r => r.GetProperty("sessionId").GetGuid() == session);
        requests.Select(r => r.ToString()).Should().NotContain(r => r.Contains(someone.ToString(), StringComparison.Ordinal), "the route template, never the values in the address");
    }

    [Fact]
    public async Task Only_the_operator_s_own_running_session_of_that_company_opens_its_data()
    {
        var company = await CompanyAsync();
        var other = await CompanyAsync();
        var anna = TestTokens.ForPlatformOperator("op-anna");
        var omar = TestTokens.ForPlatformOperator("op-omar");
        var session = await StartAsync(company, anna);
        const string Quote = "/api/v1/probe/quote";

        async Task RefusedAsync(string token, string header) =>
            await TenantMembers.ExpectProblemAsync(
                await api.SendAsync(HttpMethod.Get, Quote, token, headers: new Dictionary<string, string> { ["Karry-Support-Session"] = header }),
                HttpStatusCode.Forbidden, "support.session_invalid");

        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Get, Quote, anna), HttpStatusCode.Forbidden, "tenant.missing");
        await RefusedAsync(omar, $"{company.Id}.{session}");
        await RefusedAsync(anna, $"{other.Id}.{session}");
        await RefusedAsync(anna, $"{company.Id}.{Guid.CreateVersion7()}");
        await RefusedAsync(anna, $"{session}");
        await RefusedAsync(anna, $"{company.Id}.{session}.{session}");
        await RefusedAsync(anna, $"{Guid.Empty}.{session}");
        await RefusedAsync(TestTokens.Create("op-anna"), $"{company.Id}.{session}"); // same subject, but not a platform operator

        // ADR-0029 §5: an operator signed in without a second factor is asked to sign in again, here too.
        var withoutMfa = TestTokens.Create("op-anna", (KarryPolicies.PlatformRoleClaim, KarryPolicies.OperatorRole), (KarryPolicies.AmrClaim, "pwd"));
        await TenantMembers.ExpectProblemAsync(
            await api.SendAsync(HttpMethod.Get, Quote, withoutMfa, headers: Header(company.Id, session)), HttpStatusCode.Unauthorized, KarryPolicies.MfaRequiredCode);
        await TenantMembers.ExpectProblemAsync(
            await api.SendAsync(HttpMethod.Get, $"/api/v1/identity/tenants/{company.Id}/support-sessions", withoutMfa), HttpStatusCode.Unauthorized, KarryPolicies.MfaRequiredCode);

        // A company's own token keeps its own company: the header does not move it elsewhere.
        var otherAdmin = await api.MemberAsync(other.Id, (Roles.TenantAdmin, null));
        var own = await api.SendAsync(HttpMethod.Get, Sessions, otherAdmin, headers: Header(company.Id, session));
        (await TenantMembers.JsonAsync(own, HttpStatusCode.OK)).GetProperty("items").GetArrayLength().Should().Be(0);

        (await api.SendAsync(HttpMethod.Get, Quote, anna, headers: Header(company.Id, session))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Post, $"{Sessions}/{session}/end", company.Admin)).StatusCode.Should().Be(HttpStatusCode.OK);
        await RefusedAsync(anna, $"{company.Id}.{session}");

        var later = await StartAsync(company, anna);
        host.Clock.Advance(TimeSpan.FromHours(1));
        await RefusedAsync(anna, $"{company.Id}.{later}");
    }

    private static Dictionary<string, string> Header(Guid tenant, Guid session) => new() { ["Karry-Support-Session"] = $"{tenant}.{session}" };

    private async Task<Company> CompanyAsync()
    {
        var (id, slug) = await api.CompanyAsync();
        var subject = $"admin-{Guid.NewGuid():N}";
        await api.InsertAsync(id, "Office", [(Roles.TenantAdmin, null)], subject, employeeId: null);
        return new Company(id, slug, api.Recent(id, subject));
    }

    private async Task<string> CodeAsync(Company company)
    {
        var created = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Post, Consents, company.Admin), HttpStatusCode.Created);
        created.GetProperty("expiresAt").GetDateTimeOffset().Should().Be(host.Clock.GetUtcNow().AddMinutes(30));
        return created.GetProperty("code").GetString()!;
    }

    private async Task<Guid> StartAsync(Company company, string operatorToken) =>
        (await TenantMembers.JsonAsync(
            await api.SendAsync(HttpMethod.Post, OperatorSessions(company.Id), operatorToken, new { code = await CodeAsync(company) }), HttpStatusCode.Created))
        .GetProperty("id").GetGuid();

    private async Task EmailAsync(Guid tenant, string email)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var update = new NpgsqlCommand("UPDATE identity.users SET email = $1 WHERE tenant_id = $2", connection);
        update.Parameters.AddWithValue(email);
        update.Parameters.AddWithValue(tenant);
        await update.ExecuteNonQueryAsync(Ct);
    }

    private sealed record Company(Guid Id, string Slug, string Admin);
}
