using System.Globalization;
using System.Text.Json;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Trust.Domain;
using Karry.SharedKernel.Results;

namespace Karry.Modules.Trust.Application;

/// <summary>How many entries a search matches, and the newest of them.</summary>
public sealed record AuditSummary(long Count, long? Newest);

/// <summary>Keeps the record of an export; its event is ledgered with it, in the same transaction.</summary>
public interface IAuditExportStore
{
    Task AddAsync(AuditExport export, CancellationToken cancellationToken);
}

/// <summary>An export about to be written: how many rows, and the entries in order (read while the response streams).</summary>
public sealed record AuditExportRun(long Rows, IAsyncEnumerable<AuditEntry> Entries);

/// <summary>
/// The CSV export of the audit log (US-P1-AUD-04 AC2, ADR-0029 §2): the viewer's filters and reach, at most
/// <see cref="MaxRows"/> entries, and the export itself recorded in the ledger before a single row leaves.
/// </summary>
public sealed class AuditExports(
    AuditLogQueries queries, IAuditLog log, IAuditExportStore store, ITenantContext tenant, ICallerAccess caller, TimeProvider clock)
{
    public const long MaxRows = 100_000;

    public static readonly Error TooLarge = Error.Validation(
        "audit.export_too_large", $"An export holds at most {MaxRows.ToString("N0", CultureInfo.InvariantCulture)} entries: narrow the period or the filters.");

    private static readonly JsonSerializerOptions FilterJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public async Task<Result<AuditExportRun>> StartAsync(AuditSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var everything = search with { Cursor = null, Limit = null }; // an export is the whole result, never a page
        var query = queries.Query(everything);
        if (!query.IsSuccess)
        {
            return query.Error;
        }

        var summary = await log.SummaryAsync(query.Value, cancellationToken).ConfigureAwait(false);
        if (summary.Count > MaxRows)
        {
            return TooLarge;
        }

        var current = tenant.Current ?? throw new InvalidOperationException("An export runs for a tenant.");
        var filters = JsonSerializer.Serialize(everything, FilterJson);
        await store.AddAsync(AuditExport.Record(clock, current, filters, summary.Count, summary.Newest, caller.Subject), cancellationToken)
            .ConfigureAwait(false);

        // Up to the newest entry counted: entries written since (this export's own entry first) stay out, so the file
        // holds exactly the rows recorded.
        var upTo = query.Value with { Before = (summary.Newest ?? 0) + 1 };
        return new AuditExportRun(summary.Count, log.StreamAsync(upTo, cancellationToken));
    }
}

/// <summary>
/// The audit log as CSV for Excel (ADR-0029 §2): UTF-8 with a byte order mark, <c>;</c> between cells (the list
/// separator of French Excel), CRLF lines, RFC 4180 quoting. A cell starting with <c>= + - @</c>, a tab or a carriage
/// return gets a leading apostrophe, so a spreadsheet never runs text from the ledger as a formula.
/// </summary>
public static class AuditCsv
{
    public const string ContentType = "text/csv; charset=utf-8";

    public const char Separator = ';';

    public static readonly IReadOnlyList<string> Columns =
        ["sequence", "occurred_at", "event_type", "actor", "entity_type", "entity_id", "site_id", "changes", "event"];

    public static string Header => Line(Columns);

    public static string Row(AuditEntry entry, IReadOnlyList<AuditChange> changes, string? eventJson)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(changes);
        return Line([
            entry.Sequence.ToString(CultureInfo.InvariantCulture),
            entry.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
            entry.EventType, entry.Actor, entry.EntityType, entry.EntityId, entry.SiteId?.ToString("D"),
            Changes(changes), eventJson,
        ]);
    }

    /// <summary>One cell for all changes: <c>path: before → after</c>, separated by new lines (values as JSON).</summary>
    public static string Changes(IReadOnlyList<AuditChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return string.Join("\n", changes.Select(c => $"{c.Path}: {c.Before?.ToJsonString() ?? "null"} → {c.After?.ToJsonString() ?? "null"}"));
    }

    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([Separator, '"', '\r', '\n', ',']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }

    private static string Line(IEnumerable<string?> cells) => string.Join(Separator, cells.Select(Cell)) + "\r\n";
}
