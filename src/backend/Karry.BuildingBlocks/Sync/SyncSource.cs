namespace Karry.BuildingBlocks.Sync;

/// <summary>Whose reference data is asked for: an enrolled tablet, bound to one site (ADR-0028 §5).</summary>
public sealed record SyncScope(Guid DeviceId, Guid SiteId);

/// <summary>
/// One collection of a pull (<see cref="Name"/>): rows to keep (the tablet's row shape, serialized as is) and IDs to drop. With
/// <see cref="Replace"/> the rows are the whole collection and replace what the tablet holds (small collections whose
/// rows can be deleted, which a delta could not report).
/// </summary>
public sealed record SyncDelta(string Name, IReadOnlyList<object> Upserts, IReadOnlyList<Guid> Removes, bool Replace = false);

/// <summary>
/// A module's part of the field reference data (ADR-0028 §5), like <c>IImportTarget</c> for imports: the Sync module
/// asks every source and never reads another module's tables. Called under the tablet's tenant (row-level security).
/// </summary>
public interface ISyncSource
{
    /// <summary>
    /// Without <paramref name="since"/>, every row of the scope (a snapshot). With it, the rows changed at or after it
    /// across the tenant: those in the scope as upserts, the others (moved away, archived, deactivated) as removes.
    /// </summary>
    Task<IReadOnlyList<SyncDelta>> ChangesAsync(SyncScope scope, DateTimeOffset? since, CancellationToken cancellationToken);
}
