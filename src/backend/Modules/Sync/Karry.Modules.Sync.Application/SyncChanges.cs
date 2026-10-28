using System.Globalization;
using Karry.BuildingBlocks.Sync;
using Karry.SharedKernel.Results;

namespace Karry.Modules.Sync.Application;

/// <summary>One collection of a change set: rows to keep, IDs to drop; <see cref="Replace"/>: the rows are the whole collection.</summary>
public sealed record CollectionChanges(IReadOnlyList<object> Upserts, IReadOnlyList<Guid> Removes, bool Replace);

/// <summary>
/// The answer of <c>GET /sync/changes</c> (ADR-0028 §5, <c>@karry/offline</c> <c>ChangeSet</c>): a snapshot without a
/// cursor, else the changes since it; <see cref="Cursor"/> is what the tablet sends next time.
/// </summary>
public sealed record ChangeSet(bool Full, string Cursor, DateTimeOffset ServerTime, IReadOnlyDictionary<string, CollectionChanges> Changes);

/// <summary>
/// The field reference data of a tablet, gathered from every module's <see cref="ISyncSource"/> (ADR-0028 §5). The
/// cursor is the server time at which a pull began; the next pull asks for rows changed from 5 minutes before it, so a
/// transaction that committed late with an earlier time is not missed (the tablet applies rows idempotently).
/// </summary>
public sealed class SyncChanges(IEnumerable<ISyncSource> sources, TimeProvider clock)
{
    /// <summary>How far before the cursor a delta starts.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    /// <summary>An older cursor asks for a snapshot instead (the tablet takes one every 24 hours anyway).</summary>
    public static readonly TimeSpan CursorLifetime = TimeSpan.FromDays(7);

    public static readonly Error CursorExpired = Error.BusinessRule("sync.cursor_expired", "Download all the reference data again.");

    private const string CursorFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public async Task<Result<ChangeSet>> PullAsync(SyncScope scope, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var now = clock.GetUtcNow();
        DateTimeOffset? since = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!DateTimeOffset.TryParseExact(cursor, CursorFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) || at > now + Overlap)
            {
                return Error.Validation("sync.cursor_invalid", "This cursor was not given by the server.");
            }

            if (now - at > CursorLifetime)
            {
                return CursorExpired;
            }

            since = at - Overlap;
        }

        // One after the other: each source uses its module's DbContext of this request.
        var changes = new Dictionary<string, CollectionChanges>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (var collection in await source.ChangesAsync(scope, since, cancellationToken).ConfigureAwait(false))
            {
                changes.Add(collection.Name, new CollectionChanges(collection.Upserts, collection.Removes, collection.Replace));
            }
        }

        return new ChangeSet(since is null, now.UtcDateTime.ToString(CursorFormat, CultureInfo.InvariantCulture), now, changes);
    }
}
