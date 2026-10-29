using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Trust.Tests;

[Trait("Story", "US-P1-AUD-04")]
[Trait("TestCase", "TC-P1-AUD-005")]
public sealed class AuditCsvTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+33 1 23", "'+33 1 23")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("\rcmd", "\"'\rcmd\"")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Cells_never_run_as_formulas_and_are_quoted_when_needed(string? value, string expected) =>
        AuditCsv.Cell(value).Should().Be(expected);

    [Fact]
    public void A_row_has_every_column_in_order_with_the_time_in_utc()
    {
        var site = Guid.Parse("01970000-0000-7000-8000-0000000005a1");
        var entry = new AuditEntry(42, Guid.CreateVersion7(), "assets.asset_changed.v1", new DateTimeOffset(2026, 10, 29, 9, 30, 0, TimeSpan.FromHours(1)),
            "user-1", "asset", "a-1", site, "{}");
        var changes = new[] { new AuditChange("name", JsonValue.Create("Pelle"), JsonValue.Create("Pelle 2")), new AuditChange("hours", null, JsonValue.Create(12)) };

        var row = AuditCsv.Row(entry, changes, """{"assetId":"a-1"}""");

        row.Should().Be($"42;2026-10-29T08:30:00.000000Z;assets.asset_changed.v1;user-1;asset;a-1;{site};\"name: \"\"Pelle\"\" → \"\"Pelle 2\"\"\nhours: null → 12\";\"{{\"\"assetId\"\":\"\"a-1\"\"}}\"\r\n");
        AuditCsv.Header.Should().Be("sequence;occurred_at;event_type;actor;entity_type;entity_id;site_id;changes;event\r\n");
    }

    [Fact]
    public async Task An_export_over_the_limit_is_refused_and_leaves_no_record()
    {
        var store = new Store();
        var exports = Exports(new Log(AuditExports.MaxRows + 1), store);

        var result = await exports.StartAsync(new AuditSearch(null, null, null, null, null, null, null, null, null), TestContext.Current.CancellationToken);

        result.Error.Should().Be(AuditExports.TooLarge);
        store.Added.Should().BeEmpty();
    }

    [Fact]
    public async Task An_export_is_recorded_with_its_filters_and_rows_before_streaming_up_to_the_newest_counted_entry()
    {
        var store = new Store();
        var log = new Log(AuditExports.MaxRows, newest: 77);
        var exports = Exports(log, store);

        var result = await exports.StartAsync(
            new AuditSearch("alice", "asset", null, null, null, null, null, "12", 5), TestContext.Current.CancellationToken);

        var export = store.Added.Should().ContainSingle().Subject;
        export.Rows.Should().Be(AuditExports.MaxRows);
        export.UpToSequence.Should().Be(77);
        export.ExportedBy.Should().Be("user-1");
        export.Filters.Should().Be("""{"actor":"alice","entityType":"asset"}""", "an export is the whole result: cursor and page size are dropped");
        export.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<AuditExported>();
        result.Value.Rows.Should().Be(AuditExports.MaxRows);
        await foreach (var _ in result.Value.Entries)
        {
        }

        log.Streamed!.Before.Should().Be(78, "entries written after the count, the export's own first, stay out");
        log.Streamed.Actor.Should().Be("alice");
    }

    private static AuditExports Exports(Log log, Store store)
    {
        var tenant = new TenantContext();
        tenant.Set(new TenantId(Guid.Parse("01970000-0000-7000-8000-0000000000c1")));
        var caller = new Caller();
        return new AuditExports(new AuditLogQueries(log, caller), log, store, tenant, caller,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 29, 8, 0, 0, TimeSpan.Zero)));
    }

    private sealed class Caller : ICallerAccess
    {
        public CallerAccess Current => CallerAccess.Nobody;

        public string? Subject => "user-1";

        public DateTimeOffset? SignedInAt => null;

        public void Set(CallerAccess access, string? subject, DateTimeOffset? signedInAt) => throw new NotSupportedException();
    }

    private sealed class Store : IAuditExportStore
    {
        public List<AuditExport> Added { get; } = [];

        public Task AddAsync(AuditExport export, CancellationToken cancellationToken)
        {
            Added.Add(export);
            return Task.CompletedTask;
        }
    }

    private sealed class Log(long count, long? newest = null) : IAuditLog
    {
        public AuditQuery? Streamed { get; private set; }

        public Task<IReadOnlyList<AuditEntry>> SearchAsync(AuditQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AuditSummary> SummaryAsync(AuditQuery query, CancellationToken cancellationToken) => Task.FromResult(new AuditSummary(count, newest));

        public async IAsyncEnumerable<AuditEntry> StreamAsync(AuditQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Streamed = query;
            await Task.CompletedTask;
            yield break;
        }
    }
}
