using System.Data.Common;

namespace Karry.Modules.Assets.Contracts;

/// <summary>A site an asset was assigned to, from <see cref="From"/> until the next assignment.</summary>
public sealed record AssetSiteAssignment(Guid SiteId, DateTimeOffset From);

/// <summary>
/// A meter as other modules see it: its machine, its kind (<c>hours</c> or <c>km</c>, never a bare number), its state
/// (<c>ok</c>, <c>failed</c> …) and the machine's site assignments, oldest first.
/// </summary>
public sealed record MeterSummary(Guid MeterId, Guid AssetId, string AssetCode, string Kind, string Status, IReadOnlyList<AssetSiteAssignment> Sites)
{
    /// <summary>The site the machine was on at <paramref name="instant"/>, or null before its first assignment.</summary>
    public Guid? SiteAt(DateTimeOffset instant) => Sites.LastOrDefault(s => s.From <= instant)?.SiteId;
}

/// <summary>The current tenant's meters, for other modules (field entries). Read in the caller's unit of work (RLS).</summary>
public interface IMeterDirectory
{
    Task<MeterSummary?> FindAsync(Guid meterId, CancellationToken cancellationToken);
}

/// <summary>A machine as other modules see it: its code, its type (a code of the type catalogue) and its site assignments, oldest first.</summary>
public sealed record AssetSummary(Guid AssetId, string Code, string Type, IReadOnlyList<AssetSiteAssignment> Sites)
{
    /// <summary>The site the machine was on at <paramref name="instant"/>, or null before its first assignment.</summary>
    public Guid? SiteAt(DateTimeOffset instant) => Sites.LastOrDefault(s => s.From <= instant)?.SiteId;
}

/// <summary>The current tenant's machines, for other modules (pre-start checklists). Read in the caller's unit of work (RLS).</summary>
public interface IAssetDirectory
{
    Task<AssetSummary?> FindAsync(Guid assetId, CancellationToken cancellationToken);
}

/// <summary>
/// A meter event as other modules see it (ADR-0036 §5): <c>replaced</c> or <c>reset</c> (both values), <c>failed</c>
/// (the last value shown, if known), <c>repaired</c> (the value read at the repair).
/// </summary>
public sealed record MeterEventSummary(Guid EventId, Guid MeterId, string Kind, DateTimeOffset OccurredAt, decimal? ValueBefore, decimal? ValueAfter);

/// <summary>The current tenant's meter events, for other modules (the continuity check).</summary>
public interface IMeterEvents
{
    /// <summary>The meter's events, oldest first (by when they happened).</summary>
    Task<IReadOnlyList<MeterEventSummary>> OfMeterAsync(Guid meterId, CancellationToken cancellationToken);
}

/// <summary>
/// Told when a meter event is recorded, in the Assets outbox dispatcher's transaction and tenant (FieldOps re-evaluates
/// the meter's continuity). Delivery is at least once: a handler is idempotent.
/// </summary>
public interface IMeterEventSubscriber
{
    Task HandleAsync(MeterEventSummary meterEvent, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>How meter values are written everywhere: decimal text with at most this many decimals (ADR-0025, ADR-0034).</summary>
public static class MeterValues
{
    public const int Decimals = 1;
}
