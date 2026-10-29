using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Ledger;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Trust.Api;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Trust.Tests;

/// <summary>A stand-in business module declaring one sensitive field, so the audit log's masking can be seen.</summary>
public sealed class AuditProbeModule : IModule
{
    public const string UnitPrice = "ledgerprobe.quote.unit-price";

    public string Name => "ledgerprobe";

    public IReadOnlyList<PermissionDefinition> Permissions => [];

    public IReadOnlyList<SensitiveFieldDefinition> SensitiveFields =>
        [new(UnitPrice, new Dictionary<string, FieldAccess> { [DefaultRoles.TenantAdmin] = FieldAccess.Read })];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

public sealed record QuoteTerms(string Customer, [property: Sensitive(AuditProbeModule.UnitPrice)] decimal UnitPrice);

[EventType("ledgerprobe.quote_changed.v1")]
[AuditSubject("quote", nameof(QuoteId))]
public sealed record QuoteChanged(Guid QuoteId, QuoteTerms Before, QuoteTerms After, Guid? SiteId, string? Actor) : DomainEvent;

/// <summary><c>GET /api/v1/audit/entries</c>: the audit log with its filters, scope and masking (US-P1-AUD-04 AC1).</summary>
[Trait("Story", "US-P1-AUD-04")]
public sealed class AuditEntriesEndpointTests(LedgerDatabase postgres) : IClassFixture<LedgerDatabase>, IAsyncLifetime
{
    private static readonly Guid Tenant = Guid.Parse("01970000-0000-7000-8000-0000000000c1");
    private static readonly Guid SiteA = Guid.Parse("01970000-0000-7000-8000-0000000005a1");
    private static readonly Guid SiteB = Guid.Parse("01970000-0000-7000-8000-0000000005b1");
    private static readonly Guid Quote = Guid.Parse("01970000-0000-7000-8000-000000000a01");
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private WebApplication app = null!;
    private HttpClient client = null!;
    private KarryDatabase database = null!;

    public async ValueTask InitializeAsync()
    {
        database = await postgres.Server.NewKarryDatabaseAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Karry"] = database.App });
        builder.Services.AddKarryApiConventions();
        builder.Services.AddKarryTenancy(builder.Configuration);
        builder.Services.TrustTestIssuer();
        builder.Services.AddModules(builder.Configuration, [new TrustModule(), new AuditProbeModule()]);
        builder.Services.AddSingleton(new EventSerializer([typeof(QuoteChanged).Assembly, typeof(Domain.AuditExport).Assembly]));
        builder.Services.TreatEveryoneAs(DefaultRoles.TenantAdmin,
            new Dictionary<string, string> { ["controller"] = DefaultRoles.Controller, ["site-manager"] = DefaultRoles.SiteManager, ["operator"] = DefaultRoles.Operator },
            new Dictionary<string, Guid> { ["site-manager"] = SiteA });
        app = builder.Build();
        app.UseKarryApiConventions();
        app.UseKarryTenancy();
        app.MapModules();
        await MigrationRunner.MigrateAsync(app.Services, database.Owner, TestContext.Current.CancellationToken);
        await app.StartAsync(TestContext.Current.CancellationToken);
        client = app.GetTestClient();

        // 1: a price change on site A by alice; 2: a blast on site B by bob; 3: a company-wide event; 4: an unknown type.
        await AppendAsync(app.Services, database.App,
            Quoted(Start, new QuoteTerms("Sotacib", 41.5m), new QuoteTerms("Sotacib", 39.9m), SiteA, "alice"),
            Blasted(Start.AddDays(1), SiteB, "bob"),
            Blasted(Start.AddDays(2), null, "alice"),
            Unknown(Start.AddDays(3)));
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await app.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
    }

    [Fact]
    public async Task A_company_wide_reader_sees_every_entry_newest_first_as_its_typed_event()
    {
        var page = await SearchAsync("admin", "");

        Sequences(page).Should().Equal(4L, 3L, 2L, 1L);
        var quote = Entry(page, 1);
        quote.GetProperty("eventType").GetString().Should().Be("ledgerprobe.quote_changed.v1");
        quote.GetProperty("actor").GetString().Should().Be("alice");
        quote.GetProperty("entityType").GetString().Should().Be("quote");
        quote.GetProperty("entityId").GetString().Should().Be(Quote.ToString());
        quote.GetProperty("siteId").GetGuid().Should().Be(SiteA);
        quote.GetProperty("event").GetProperty("after").GetProperty("unitPrice").GetDecimal().Should().Be(39.9m);
        quote.GetProperty("event").TryGetProperty("eventId", out _).Should().BeFalse("already on the entry");
        var change = quote.GetProperty("changes").EnumerateArray().Should().ContainSingle().Subject;
        change.GetProperty("path").GetString().Should().Be("unitPrice");
        change.GetProperty("before").GetDecimal().Should().Be(41.5m);
        change.GetProperty("after").GetDecimal().Should().Be(39.9m);
        Entry(page, 2).GetProperty("changes").GetArrayLength().Should().Be(0, "a blast has no before and after");
        Entry(page, 4).GetProperty("event").ValueKind.Should().Be(JsonValueKind.Null, "this version does not know the event type");
    }

    [Fact]
    public async Task Masked_fields_stay_masked_in_the_event_and_out_of_the_changes()
    {
        var quote = Entry(await SearchAsync("controller", ""), 1);

        var after = quote.GetProperty("event").GetProperty("after");
        after.TryGetProperty("unitPrice", out _).Should().BeFalse();
        after.GetProperty("maskedFields").EnumerateArray().Select(f => f.GetString()).Should().Equal("unitPrice");
        after.GetProperty("customer").GetString().Should().Be("Sotacib");
        quote.GetProperty("changes").GetArrayLength().Should().Be(0, "the masked price must not show, not even that it changed");
        quote.GetRawText().Should().NotContain("39.9").And.NotContain("41.5");
    }

    [Fact]
    public async Task A_reader_on_some_sites_sees_only_those_sites_entries()
    {
        var page = await SearchAsync("site-manager", "");

        Sequences(page).Should().Equal(1L);
        Sequences(await SearchAsync("site-manager", $"?siteId={SiteB}")).Should().BeEmpty("asking for another site does not widen the reach");
    }

    [Theory]
    [InlineData("?actor=alice", new long[] { 3, 1 })]
    [InlineData("?entityType=blast", new long[] { 3, 2 })]
    [InlineData("?entityType=quote&entityId=01970000-0000-7000-8000-000000000a01", new long[] { 1 })]
    [InlineData("?eventType=ledgerprobe.blast_recorded.v1&actor=alice", new long[] { 3 })]
    [InlineData("?siteId=01970000-0000-7000-8000-0000000005b1", new long[] { 2 })]
    [InlineData("?from=2026-10-02T08:00:00Z&to=2026-10-03T08:00:00Z", new long[] { 3, 2 })]
    [InlineData("?actor=%20", new long[] { 4, 3, 2, 1 })]
    public async Task Filters_combine(string query, long[] expected) =>
        Sequences(await SearchAsync("admin", query)).Should().Equal(expected);

    [Fact]
    public async Task Pages_follow_each_other_without_gaps_or_repeats()
    {
        var first = await SearchAsync("admin", "?limit=3");
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await SearchAsync("admin", $"?limit=3&cursor={cursor}");

        Sequences(first).Should().Equal(4L, 3L, 2L);
        cursor.Should().Be("2");
        Sequences(second).Should().Equal(1L);
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("?limit=0", "audit.limit_invalid")]
    [InlineData("?limit=201", "audit.limit_invalid")]
    [InlineData("?cursor=abc", "audit.cursor_invalid")]
    [InlineData("?cursor=-1", "audit.cursor_invalid")]
    [InlineData("?from=2026-10-03T00:00:00Z&to=2026-10-02T00:00:00Z", "audit.range_invalid")]
    public async Task Invalid_searches_are_refused(string query, string code)
    {
        using var response = await SendAsync("admin", query, Tenant);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain(code);
    }

    [Fact]
    public async Task Without_the_permission_the_log_is_refused_and_another_company_sees_none_of_it()
    {
        using var refused = await SendAsync("operator", "", Tenant);
        using var other = await SendAsync("admin", "", Guid.CreateVersion7());

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Sequences(await other.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    [Trait("TestCase", "TC-P1-AUD-005")]
    public async Task The_export_is_a_csv_for_excel_and_is_itself_ledgered()
    {
        using var response = await ExportAsync("admin", "");
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var lines = System.Text.Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        response.Content.Headers.ContentDisposition!.FileNameStar.Should().StartWith("karry-audit-").And.EndWith(".csv");
        bytes[..3].Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF });
        lines[0].Should().Be("sequence;occurred_at;event_type;actor;entity_type;entity_id;site_id;changes;event");
        lines.Skip(1).Select(l => l.Split(';')[0]).Should().Equal("4", "3", "2", "1");
        lines[4].Should().Contain("unitPrice: 41.5 → 39.9");

        var exported = Entry(await SearchAsync("admin", "?eventType=trust.audit_exported.v1"), 5);
        exported.GetProperty("actor").GetString().Should().Be("admin");
        exported.GetProperty("entityType").GetString().Should().Be("audit_export");
        exported.GetProperty("event").GetProperty("rows").GetInt64().Should().Be(4);
        exported.GetProperty("event").GetProperty("upToSequence").GetInt64().Should().Be(4, "the export does not contain its own entry");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-AUD-005")]
    public async Task The_export_keeps_masking_reach_and_filters()
    {
        var masked = await (await ExportAsync("controller", "")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var site = await (await ExportAsync("site-manager", "")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var filtered = await (await ExportAsync("admin", "?actor=bob")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        masked.Should().NotContain("39.9").And.NotContain("41.5").And.Contain("maskedFields");
        Rows(site).Should().Equal("1");
        Rows(filtered).Should().Equal("2");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-AUD-005")]
    public async Task Text_from_the_ledger_never_reaches_excel_as_a_formula()
    {
        await AppendAsync(app.Services, database.App, Blasted(Start.AddDays(4), null, "=HYPERLINK(\"http://evil\";\"x\")"));

        var csv = await (await ExportAsync("admin", "?entityType=blast")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        csv.Should().Contain("\"'=HYPERLINK(\"\"http://evil\"\";\"\"x\"\")\"");
        csv.Split("\r\n").Should().NotContain(l => l.Contains(";=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invalid_export_is_refused_and_leaves_no_trace()
    {
        using var response = await ExportAsync("admin", "?from=2026-10-03T00:00:00Z&to=2026-10-02T00:00:00Z");
        using var refused = await ExportAsync("operator", "");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Sequences(await SearchAsync("admin", "")).Should().Equal(4L, 3L, 2L, 1L);
    }

    private static string[] Rows(string csv) =>
        [.. csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(';')[0])];

    private Task<HttpResponseMessage> ExportAsync(string subject, string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/audit/entries/export.csv" + query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(Tenant, subject));
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static long[] Sequences(JsonElement page) =>
        [.. page.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("sequence").GetInt64())];

    private static JsonElement Entry(JsonElement page, long sequence) =>
        page.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("sequence").GetInt64() == sequence);

    private async Task<JsonElement> SearchAsync(string subject, string query)
    {
        using var response = await SendAsync(subject, query, Tenant);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private Task<HttpResponseMessage> SendAsync(string subject, string query, Guid tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/audit/entries" + query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant, subject));
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static LedgerRecord Quoted(DateTimeOffset at, QuoteTerms before, QuoteTerms after, Guid site, string actor) =>
        Record(new QuoteChanged(Quote, before, after, site, actor) { OccurredAt = at });

    private static LedgerRecord Blasted(DateTimeOffset at, Guid? site, string actor) =>
        Record(new BlastRecorded(Guid.CreateVersion7(), 12, site, actor) { OccurredAt = at });

    private static LedgerRecord Unknown(DateTimeOffset at) =>
        new(new TenantId(Tenant), Guid.CreateVersion7(), "ledgerprobe.from_the_future.v1", "{}", at);

    private static LedgerRecord Record(DomainEvent domainEvent) =>
        new(new TenantId(Tenant), domainEvent.EventId, EventTypeAttribute.NameOf(domainEvent.GetType()), EventSerializer.Serialize(domainEvent),
            domainEvent.OccurredAt, AuditSubject.Of(domainEvent));

    private static async Task AppendAsync(IServiceProvider services, string connectionString, params LedgerRecord[] records)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var setTenant = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{Tenant}', true)", connection, transaction))
        {
            await setTenant.ExecuteNonQueryAsync(ct);
        }

        await services.GetRequiredService<ILedgerWriter>().AppendAsync(connection, transaction, new TenantId(Tenant), records, ct);
        await transaction.CommitAsync(ct);
    }
}
