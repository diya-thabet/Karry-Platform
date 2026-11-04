using Karry.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Karry.BuildingBlocks.Persistence;

/// <summary>
/// Optimistic concurrency for records people edit (hardening plan H-C1, api-standards "Concurrency"): an aggregate's
/// <c>Version</c> is PostgreSQL's <c>xmin</c> of its row, which changes with every update (no column, no trigger). The
/// API sends it as <c>ETag</c> and <c>version</c>; an update carrying <c>If-Match</c> is applied only to that version.
/// The service compares it with the loaded record, and EF Core checks it again in the <c>UPDATE … WHERE xmin = …</c>,
/// so neither a slow form nor a race between load and save can overwrite someone else's change.
/// </summary>
public static class RecordVersions
{
    public const string ConflictCode = "request.version_conflict";

    /// <summary>The record changed since the caller read it; nothing was saved.</summary>
    public static readonly Error Conflict = Error.Conflict(ConflictCode, "Someone else changed this record since you opened it. Nothing was saved.");

    /// <summary>Maps <paramref name="version"/> to <c>xmin</c> as the row version.</summary>
    public static PropertyBuilder<uint> IsRecordVersion(this PropertyBuilder<uint> version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }

    /// <summary>True when the caller expects no particular version, or exactly this one.</summary>
    public static bool Matches(uint current, uint? expected) => expected is null || expected == current;
}
