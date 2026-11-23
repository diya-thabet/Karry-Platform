using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Verification.Contracts;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Verification.Tests;

/// <summary>
/// Review items (ADR-0037 §1, §2): another module submits a record version in its own transaction; one item per
/// version, ledgered, never deleted; the queue lists the items of the caller's sites, oldest first, never another
/// company's.
/// </summary>
[Trait("Story", "US-P2-VER-02")]
public sealed class ReviewItemsTests(VerificationHost host) : IClassFixture<VerificationHost>
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 23, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ReviewSubmission Entry(Guid site, Guid? root = null, int version = 2, params string[] flags) =>
        new(ReviewRecordTypes.ShiftEntry, root ?? Guid.CreateVersion7(), version, "sha256:" + new string('c', 64), site, Guid.CreateVersion7(),
            Guid.CreateVersion7(), Morning, Morning.AddHours(8), flags);

    [Fact]
    public async Task A_submitted_version_becomes_one_pending_item_ledgered_and_submitting_it_again_changes_nothing()
    {
        var tenant = await ProvisionAsync();
        var entry = Entry(Guid.CreateVersion7(), flags: "OVERLAP");

        await SubmitAsync(tenant, entry);
        await SubmitAsync(tenant, entry);
        await SubmitAsync(tenant, entry with { RecordVersion = 3 });

        var items = await ListAsync(tenant, "");
        items.Select(i => i.GetProperty("recordVersion").GetInt32()).Should().BeEquivalentTo([2, 3]);
        var item = items.Single(i => i.GetProperty("recordVersion").GetInt32() == 2);
        (item.GetProperty("recordType").GetString(), item.GetProperty("recordId").GetGuid(), item.GetProperty("status").GetString())
            .Should().Be((ReviewRecordTypes.ShiftEntry, entry.RecordId, "pending"));
        item.GetProperty("recordHash").GetString().Should().Be(entry.RecordHash);
        item.GetProperty("producedBy").GetGuid().Should().Be(entry.ProducedBy);
        item.GetProperty("flags").EnumerateArray().Select(f => f.GetString()).Should().Equal("OVERLAP");
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'verification.review_item_created.v1'"))
            .Should().Be(2, "one per version, the repeated submission adds nothing");
    }

    [Fact]
    public async Task An_item_is_never_deleted()
    {
        var tenant = await ProvisionAsync();
        await SubmitAsync(tenant, Entry(Guid.CreateVersion7()));

        await FluentActions.Awaiting(() => ExecuteAsAppAsync(tenant, "DELETE FROM verification.review_items")).Should().ThrowAsync<PostgresException>();
        await FluentActions.Awaiting(() => ExecuteAsAppAsync(tenant, "TRUNCATE verification.review_items")).Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task The_queue_pages_oldest_first_filters_by_status_and_rejects_what_it_does_not_know()
    {
        var tenant = await ProvisionAsync();
        var site = Guid.CreateVersion7();
        var roots = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var entry = Entry(site);
            roots.Add(entry.RecordId);
            await SubmitAsync(tenant, entry);
        }

        var first = await JsonAsync(tenant, "?limit=2");
        var second = await JsonAsync(tenant, $"?limit=2&cursor={first.GetProperty("nextCursor").GetGuid()}");

        var pages = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(i => i.GetProperty("recordId").GetGuid()).ToList();
        first.GetProperty("items").GetArrayLength().Should().Be(2);
        pages.Should().BeEquivalentTo(roots);
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        (await ListAsync(tenant, "?status=reviewed")).Should().BeEmpty();
        (await ListAsync(tenant, "?status=all")).Should().HaveCount(3);
        (await ListAsync(tenant, "?recordType=weighbridge.ticket")).Should().BeEmpty();
        (await GetAsync(tenant, "?status=done")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync(tenant, "?limit=0")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync(tenant, "?limit=201")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_controller_reads_the_items_of_their_sites_an_operator_none_and_another_company_never()
    {
        var (tenant, other) = (await ProvisionAsync(), await ProvisionAsync());
        var (north, south) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var mine = Entry(north);
        await SubmitAsync(tenant, mine);
        await SubmitAsync(tenant, Entry(south));
        host.SiteOf[VerificationHost.Controller] = north;

        var controller = await ListAsync(tenant, "", TestTokens.ForTenant(tenant, VerificationHost.Controller));
        var field = await GetAsync(tenant, "", TestTokens.ForTenant(tenant, VerificationHost.Operator));
        var elsewhere = await ListAsync(other, "");

        controller.Select(i => i.GetProperty("recordId").GetGuid()).Should().Equal(mine.RecordId);
        field.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        elsewhere.Should().BeEmpty();
    }

    /// <summary>A module submitting a version in its own transaction, as FieldOps does when an entry ends (ADR-0037 §1).</summary>
    private async Task SubmitAsync(Guid tenant, ReviewSubmission submission)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        await using var connection = new NpgsqlConnection(host.Database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<IReviewIntake>().SubmitAsync(connection, transaction, submission, Ct);
        await transaction.CommitAsync(Ct);
    }

    private async Task<List<JsonElement>> ListAsync(Guid tenant, string query, string? token = null) =>
        [.. (await JsonAsync(tenant, query, token)).GetProperty("items").EnumerateArray()];

    private async Task<JsonElement> JsonAsync(Guid tenant, string query, string? token = null)
    {
        using var response = await GetAsync(tenant, query, token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<HttpResponseMessage> GetAsync(Guid tenant, string query, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/verification/items{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.ForTenant(tenant, "admin-1"));
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"v-{Guid.NewGuid():N}"[..20];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name = $"Carrière {slug}" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForPlatformOperator());
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Runs SQL as the application's role in the tenant, as the app would.</summary>
    private async Task ExecuteAsAppAsync(Guid tenant, string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{tenant}', true)", connection, transaction))
        {
            await set.ExecuteNonQueryAsync(Ct);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(Ct);
        await transaction.CommitAsync(Ct);
    }
}
