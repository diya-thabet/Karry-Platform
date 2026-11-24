using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Verification.Contracts;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Verification.Tests;

/// <summary>
/// Review policies (US-P2-VER-05, ADR-0037 §5): versioned and ledgered, published by a company administrator with a
/// recent sign-in; every item keeps the version it started under; auto-approval is an action of the policy, naming who
/// published it.
/// </summary>
[Trait("Story", "US-P2-VER-05")]
public sealed class ReviewPoliciesTests(VerificationHost host) : IClassFixture<VerificationHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Terms(bool autoApprove = false, int slaHours = 24, decimal fourEyesHours = 2m, string escalateTo = "site-manager", DateTimeOffset? effectiveFrom = null) =>
        new { autoApprove, fourEyesHours, fourEyesShare = 0.05m, slaHours, escalateTo, effectiveFrom };

    [Fact]
    public async Task A_company_reviews_by_the_defaults_until_an_administrator_publishes_a_version_with_a_recent_sign_in()
    {
        var tenant = await ProvisionAsync();

        var defaults = (await PoliciesAsync(tenant)).Single();
        using var notRecent = await PublishAsync(tenant, Terms(), TestTokens.ForTenant(tenant, "admin-1"));
        using var controller = await PublishAsync(tenant, Terms(), Recent(tenant, VerificationHost.Controller));
        using var invalid = await PublishAsync(tenant, Terms(slaHours: 0));
        using var badRole = await PublishAsync(tenant, Terms(escalateTo: "operator"));
        using var backdated = await PublishAsync(tenant, Terms(effectiveFrom: DateTimeOffset.UtcNow.AddDays(-1)));
        using var unknown = await PublishAsync(tenant, Terms(), path: "weighbridge.ticket");
        using var published = await PublishAsync(tenant, Terms(slaHours: 12));

        (defaults.GetProperty("recordType").GetString(), defaults.GetProperty("current").GetProperty("version").GetInt32(), defaults.GetProperty("current").GetProperty("slaHours").GetInt32())
            .Should().Be((ReviewRecordTypes.ShiftEntry, 0, 24));
        notRecent.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        controller.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeAsync(invalid)).Should().Be("verification.policy_invalid");
        (await CodeAsync(badRole)).Should().Be("verification.policy_invalid");
        (await CodeAsync(backdated)).Should().Be("verification.policy_backdated");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await published.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetInt32().Should().Be(1);
        var current = (await PoliciesAsync(tenant)).Single();
        (current.GetProperty("current").GetProperty("version").GetInt32(), current.GetProperty("current").GetProperty("publishedBy").GetString()).Should().Be((1, "admin-1"));
        (await RowsAsync($"SELECT payload::jsonb->>'slaHours' FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'verification.review_policy_published.v1'"))
            .Should().Equal("12");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-VER-006")]
    public async Task An_auto_approving_policy_approves_unflagged_entries_as_itself_naming_who_published_it_and_leaves_flagged_ones_to_a_person()
    {
        var tenant = await ProvisionAsync();
        (await PublishAsync(tenant, Terms(autoApprove: true))).Dispose();

        var clean = await SubmitAsync(tenant, []);
        var flagged = await SubmitAsync(tenant, ["OVERLAP"]);

        var items = await ItemsAsync(tenant);
        Status(items, clean).Should().Be("approved");
        Status(items, flagged).Should().Be("pending");
        (await RowsAsync($"SELECT kind || '|' || actor FROM verification.review_actions WHERE tenant_id = '{tenant}'")).Should().Equal("auto_approve|policy:1");
        (await RowsAsync($"SELECT (payload::jsonb->>'policyVersion') || '|' || (payload::jsonb->>'policyPublishedBy') FROM audit.ledger_entries WHERE tenant_id = '{tenant}' "
            + "AND event_type = 'verification.review_item_auto_approved.v1'")).Should().Equal("1|admin-1");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-VER-007")]
    public async Task An_item_keeps_the_policy_version_it_started_under_when_a_new_one_is_published()
    {
        var tenant = await ProvisionAsync();
        (await PublishAsync(tenant, Terms(slaHours: 24))).Dispose();
        var before = await SubmitAsync(tenant, []);
        (await PublishAsync(tenant, Terms(slaHours: 4))).Dispose();
        var after = await SubmitAsync(tenant, []);

        var items = await ItemsAsync(tenant);
        var first = items.Single(i => i.GetProperty("recordId").GetGuid() == before);
        var second = items.Single(i => i.GetProperty("recordId").GetGuid() == after);
        first.GetProperty("policyVersion").GetInt32().Should().Be(1);
        second.GetProperty("policyVersion").GetInt32().Should().Be(2);
        (first.GetProperty("dueAt").GetDateTimeOffset() - first.GetProperty("createdAt").GetDateTimeOffset()).Should().Be(TimeSpan.FromHours(24));
        (second.GetProperty("dueAt").GetDateTimeOffset() - second.GetProperty("createdAt").GetDateTimeOffset()).Should().Be(TimeSpan.FromHours(4));
    }

    [Fact]
    [Trait("Story", "US-P2-VER-06")]
    [Trait("TestCase", "TC-P2-VER-005")]
    public async Task An_entry_waiting_past_its_due_time_escalates_once_to_the_policys_role_and_a_decided_one_does_not()
    {
        var tenant = await ProvisionAsync();
        (await PublishAsync(tenant, Terms(slaHours: 2, escalateTo: "executive"))).Dispose();
        var waiting = await SubmitAsync(tenant, []);
        var decided = await SubmitAsync(tenant, []);
        var decidedItem = (await ItemsAsync(tenant)).Single(i => i.GetProperty("recordId").GetGuid() == decided);
        using (var approve = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/verification/items/{decidedItem.GetProperty("id").GetGuid()}/approve"))
        {
            approve.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant, "admin-1"));
            (await host.Client.SendAsync(approve, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var escalate = host.Services.GetRequiredService<Application.EscalateOverdueReviews>();
        var early = await escalate.RunAtAsync(DateTimeOffset.UtcNow.AddHours(1), Ct);
        var due = await escalate.RunAtAsync(DateTimeOffset.UtcNow.AddHours(3), Ct);
        var again = await escalate.RunAtAsync(DateTimeOffset.UtcNow.AddHours(4), Ct);

        early.Escalated.Should().Be(0, "not due yet");
        due.Escalated.Should().BeGreaterThanOrEqualTo(1);
        again.FailedTenants.Should().Be(0);
        var items = await ItemsAsync(tenant);
        items.Single(i => i.GetProperty("recordId").GetGuid() == waiting).GetProperty("escalatedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
        items.Single(i => i.GetProperty("recordId").GetGuid() == decided).GetProperty("escalatedAt").ValueKind.Should().Be(JsonValueKind.Null);
        (await ListAsync(tenant, "/api/v1/verification/items?overdue=true")).Select(i => i.GetProperty("recordId").GetGuid()).Should().Equal(waiting);
        (await RowsAsync($"SELECT (payload::jsonb->>'escalateTo') || '|' || (payload::jsonb->>'policyVersion') FROM audit.ledger_entries WHERE tenant_id = '{tenant}' "
            + "AND event_type = 'verification.review_item_escalated.v1'")).Should().Equal(["executive|1"], "escalated once, to the role of the policy it started under");
    }

    private static string Status(List<JsonElement> items, Guid record) =>
        items.Single(i => i.GetProperty("recordId").GetGuid() == record).GetProperty("status").GetString()!;

    private async Task<Guid> SubmitAsync(Guid tenant, IReadOnlyList<string> flags)
    {
        var now = DateTimeOffset.UtcNow;
        var submission = new ReviewSubmission(ReviewRecordTypes.ShiftEntry, Guid.CreateVersion7(), 2, "sha256:" + new string('d', 64), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), now.AddHours(-8), now, flags);
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        await using var connection = new NpgsqlConnection(host.Database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<IReviewIntake>().SubmitAsync(connection, transaction, submission, Ct);
        await transaction.CommitAsync(Ct);
        return submission.RecordId;
    }

    private Task<HttpResponseMessage> PublishAsync(Guid tenant, object terms, string? token = null, string path = ReviewRecordTypes.ShiftEntry)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/verification/policies/{path}") { Content = JsonContent.Create(terms) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Recent(tenant, "admin-1"));
        return host.Client.SendAsync(request, Ct);
    }

    private async Task<List<JsonElement>> PoliciesAsync(Guid tenant) => await ListAsync(tenant, "/api/v1/verification/policies");

    private async Task<List<JsonElement>> ItemsAsync(Guid tenant) => await ListAsync(tenant, "/api/v1/verification/items?status=all");

    private async Task<List<JsonElement>> ListAsync(Guid tenant, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant, "admin-1"));
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").EnumerateArray()];
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    private static string Recent(Guid tenant, string subject) =>
        TestTokens.Create(subject, (TenantResolutionMiddleware.TenantClaim, tenant.ToString()),
            (PermissionMiddleware.AuthTimeClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"p-{Guid.NewGuid():N}"[..20];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name = $"Carrière {slug}" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForPlatformOperator());
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<List<string>> RowsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
