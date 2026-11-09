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

/// <summary>How meter values are written everywhere: decimal text with at most this many decimals (ADR-0025, ADR-0034).</summary>
public static class MeterValues
{
    public const int Decimals = 1;
}
