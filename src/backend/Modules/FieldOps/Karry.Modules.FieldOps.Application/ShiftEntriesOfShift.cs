using Karry.Modules.FieldOps.Contracts;

namespace Karry.Modules.FieldOps.Application;

/// <summary>A shift's entries for documents (ADR-0040): the current versions, through the portal's reader (RLS).</summary>
public sealed class ShiftEntriesOfShift(IShiftEntryReader reader) : IShiftEntriesOfShift
{
    public async Task<IReadOnlyList<ShiftReportEntry>> OfShiftAsync(Guid siteId, DateOnly shiftDate, string shiftCode, CancellationToken cancellationToken)
    {
        var versions = await reader.SearchAsync(
            new EntryQuery(null, siteId, shiftDate, shiftCode, null, null, null, null, null, IShiftEntriesOfShift.MaxEntries + 1), cancellationToken).ConfigureAwait(false);
        return [.. versions.OrderBy(v => v.StartedAt).ThenBy(v => v.RootId).Select(v => new ShiftReportEntry(
            v.RootId, v.Version, v.AssetId, v.OperatorId, v.MeterKind, v.StartedAt, v.StartValue, v.EndedAt, v.EndValue,
            v.Status.ToString().ToLowerInvariant(), v.Flags, v.EnvelopeId, v.CorrectedBy, v.Estimated) { DeviceId = v.DeviceId })];
    }
}
