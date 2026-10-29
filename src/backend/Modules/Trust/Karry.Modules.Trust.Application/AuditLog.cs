using System.Globalization;
using System.Text.Json.Nodes;
using Karry.BuildingBlocks.Authorization;
using Karry.SharedKernel.Results;

namespace Karry.Modules.Trust.Application;

/// <summary>The audit viewer's filters (US-P1-AUD-04 AC1), as the API receives them. <c>Cursor</c>: the last page's.</summary>
public sealed record AuditSearch(
    string? Actor, string? EntityType, string? EntityId, Guid? SiteId, string? EventType,
    DateTimeOffset? From, DateTimeOffset? To, string? Cursor, int? Limit);

/// <summary>
/// A search as the store runs it, newest first: entries with a sequence below <c>Before</c>. <c>Sites</c> null means
/// every entry of the company; otherwise only entries of those sites (entries without a site are company-wide).
/// </summary>
public sealed record AuditQuery(
    string? Actor, string? EntityType, string? EntityId, Guid? SiteId, string? EventType,
    DateTimeOffset? From, DateTimeOffset? To, IReadOnlyCollection<Guid>? Sites, long? Before, int Take);

/// <summary>A ledger entry with what it is about. <c>Payload</c> is the stored canonical JSON (never sent as is).</summary>
public sealed record AuditEntry(
    long Sequence, Guid EventId, string EventType, DateTimeOffset OccurredAt,
    string? Actor, string? EntityType, string? EntityId, Guid? SiteId, string Payload);

public sealed record AuditPage(IReadOnlyList<AuditEntry> Entries, string? NextCursor);

/// <summary>Reads the current tenant's ledger with its subject index (row-level security scopes it to the tenant).</summary>
public interface IAuditLog
{
    Task<IReadOnlyList<AuditEntry>> SearchAsync(AuditQuery query, CancellationToken cancellationToken);

    Task<AuditSummary> SummaryAsync(AuditQuery query, CancellationToken cancellationToken);

    /// <summary>Every entry the query matches, newest first, streamed (<c>Take</c> is ignored).</summary>
    IAsyncEnumerable<AuditEntry> StreamAsync(AuditQuery query, CancellationToken cancellationToken);

    /// <summary>The distinct actors of the entries the query matches (at most <c>Take</c>), in order.</summary>
    Task<IReadOnlyList<string>> ActorsAsync(AuditQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// The audit log (US-P1-AUD-04, ADR-0029 §2) for holders of <c>audit.ledger.read</c>: company-wide holders see every
/// entry; holders on some sites only the entries of those sites (fail-closed: an entry without a site is company-wide).
/// </summary>
public sealed class AuditLogQueries(IAuditLog log, ICallerAccess caller)
{
    public const string ReadPermission = "audit.ledger.read";

    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    public const int MaxActors = 500;

    public static readonly Error LimitInvalid = Error.Validation("audit.limit_invalid", $"Ask for 1 to {MaxLimit} entries.");

    public static readonly Error CursorInvalid = Error.Validation("audit.cursor_invalid", "Use the cursor of the previous page.");

    public static readonly Error RangeInvalid = Error.Validation("audit.range_invalid", "The start of the period must be before its end.");

    public static readonly Error FilterTooLong = Error.Validation("audit.filter_invalid", "A filter is at most 200 characters.");

    public async Task<Result<AuditPage>> SearchAsync(AuditSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var query = Query(search);
        if (!query.IsSuccess)
        {
            return query.Error;
        }

        var take = query.Value.Take - 1;
        var found = await log.SearchAsync(query.Value, cancellationToken).ConfigureAwait(false);
        return found.Count > take
            ? new AuditPage([.. found.Take(take)], found[take - 1].Sequence.ToString(CultureInfo.InvariantCulture))
            : new AuditPage(found, null);
    }

    /// <summary>Who appears in the entries the caller may read, for the viewer's "person" filter (at most <see cref="MaxActors"/>).</summary>
    public Task<IReadOnlyList<string>> ActorsAsync(CancellationToken cancellationToken)
    {
        var scope = caller.Current.ScopeOf(ReadPermission);
        return log.ActorsAsync(
            new AuditQuery(null, null, null, null, null, null, null, scope.TenantWide ? null : scope.Sites, null, MaxActors), cancellationToken);
    }

    /// <summary>The store query for a search and the caller's reach (one more row than the page, to know if more follow).</summary>
    public Result<AuditQuery> Query(AuditSearch search)
    {
        ArgumentNullException.ThrowIfNull(search);
        var take = search.Limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            return LimitInvalid;
        }

        long? before = null;
        if (search.Cursor is { } cursor)
        {
            if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence < 1)
            {
                return CursorInvalid;
            }

            before = sequence;
        }

        if (search.From is { } from && search.To is { } to && from > to)
        {
            return RangeInvalid;
        }

        if (new[] { search.Actor, search.EntityType, search.EntityId, search.EventType }.Any(f => f?.Length > 200))
        {
            return FilterTooLong;
        }

        var scope = caller.Current.ScopeOf(ReadPermission);
        return new AuditQuery(
            Blank(search.Actor), Blank(search.EntityType), Blank(search.EntityId), search.SiteId, Blank(search.EventType),
            search.From, search.To, scope.TenantWide ? null : scope.Sites, before, take + 1);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One changed value between an event's <c>before</c> and <c>after</c>: a JSON path and both values.</summary>
public sealed record AuditChange(string Path, JsonNode? Before, JsonNode? After);

/// <summary>
/// The before/after diff of an event (US-P1-AUD-04 AC1), computed on the event as the caller receives it (already
/// masked): a masked field is absent on both sides and never listed. Objects are compared field by field, lists and
/// values as a whole.
/// </summary>
public static class AuditChanges
{
    private const string MaskedFields = "maskedFields";

    /// <summary>The changes of an event with <c>before</c> and <c>after</c> properties; none for other events.</summary>
    public static IReadOnlyList<AuditChange> Of(JsonObject? domainEvent)
    {
        if (domainEvent is null || !domainEvent.ContainsKey("before") || !domainEvent.ContainsKey("after"))
        {
            return [];
        }

        var changes = new List<AuditChange>();
        Compare(string.Empty, domainEvent["before"], domainEvent["after"], changes);
        return changes;
    }

    private static void Compare(string path, JsonNode? before, JsonNode? after, List<AuditChange> changes)
    {
        if (before is JsonObject left && after is JsonObject right)
        {
            foreach (var name in left.Select(p => p.Key).Union(right.Select(p => p.Key), StringComparer.Ordinal).Where(n => n != MaskedFields))
            {
                Compare(path.Length == 0 ? name : $"{path}.{name}", left[name], right[name], changes);
            }

            return;
        }

        if (!JsonNode.DeepEquals(before, after))
        {
            changes.Add(new AuditChange(path, before?.DeepClone(), after?.DeepClone()));
        }
    }
}
