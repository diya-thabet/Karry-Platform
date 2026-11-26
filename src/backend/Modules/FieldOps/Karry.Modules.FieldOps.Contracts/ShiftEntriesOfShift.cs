namespace Karry.Modules.FieldOps.Contracts;

/// <summary>
/// One entry of a shift as a document prints it (the shift report, ADR-0040): its current version's machine, operator,
/// meter kind (<c>hours</c>, <c>km</c>, …), start and end, status (<c>open</c>, <c>closed</c>), flags, the signature
/// envelope, and the controller who corrected it, if one did. Δ is <c>EndValue − StartValue</c>.
/// </summary>
public sealed record ShiftReportEntry(
    Guid RootId, int Version, Guid AssetId, Guid OperatorId, string MeterKind, DateTimeOffset StartedAt, decimal StartValue,
    DateTimeOffset? EndedAt, decimal? EndValue, string Status, IReadOnlyList<string> Flags, Guid EnvelopeId, string? CorrectedBy, bool Estimated);

/// <summary>The current company's entries of one shift on one site (RLS), oldest start first.</summary>
public interface IShiftEntriesOfShift
{
    /// <summary>At most <see cref="MaxEntries"/>; a shift with more is refused by the caller rather than printed short.</summary>
    Task<IReadOnlyList<ShiftReportEntry>> OfShiftAsync(Guid siteId, DateOnly shiftDate, string shiftCode, CancellationToken cancellationToken);

    public const int MaxEntries = 2000;
}
