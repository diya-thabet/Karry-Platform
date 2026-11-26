using System.Globalization;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Assets.Contracts;
using Karry.Modules.Documents.Domain;
using Karry.Modules.FieldOps.Contracts;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Tenancy.Contracts;
using Karry.Modules.Trust.Contracts;
using Karry.Modules.Verification.Contracts;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Documents.Application;

/// <summary>Documents' permission (ADR-0040).</summary>
public static class DocumentsPermissions
{
    /// <summary>
    /// Read and issue the shift reports of the caller's sites. Field roles hold it too: a report is masked for its reader
    /// (TC-P2-DOC-003), so a crew lead can hand the day's report to a client without seeing what their role may not.
    /// </summary>
    public const string ShiftReportsRead = "documents.shift-reports.read";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(ShiftReportsRead, ScopeKind.Site, privileged: false,
            Roles.TenantAdmin, Roles.Executive, Roles.Finance, Roles.Controller, Roles.SiteManager, Roles.MaintenancePlanner, Roles.Operator),
    ];
}

/// <summary>
/// One entry of the report, before masking: every value the report may print, as text, and its site, so a reader's
/// field access on that site applies (a site manager reads what their role may read on their sites).
/// </summary>
public sealed record ShiftReportRow(
    Guid RootId, Guid? SiteId, string Machine, string Operator, string Start, string End, string Delta, string Unit, string Status, string Review,
    string Controller, string Flags, string Signature) : ISiteScoped;

/// <summary>The report's columns (ADR-0040 §2): codes the PDF writes in the reader's words; a sensitive one names its field.</summary>
public static class ShiftReportColumns
{
    public static IReadOnlyList<ExportColumn<ShiftReportRow>> All { get; } =
    [
        new("machine", r => r.Machine),
        new("operator", r => r.Operator),
        new("start", r => r.Start),
        new("end", r => r.End),
        new("delta", r => r.Delta),
        new("unit", r => r.Unit),
        new("status", r => r.Status),
        new("review", r => r.Review),
        new("controller", r => r.Controller),
        new("flags", r => r.Flags),
        new("signature", r => r.Signature),
    ];
}

/// <summary>
/// The shift report's content (ADR-0040 §2, US-P2-SIG-05 AC1): a site's entries of one shift, read only through the
/// owning modules' contracts in the current company (RLS), then masked for the reader with the API's decision
/// (<see cref="SensitiveExport"/>): a column the reader may never see is absent, a cell they may not see is masked.
/// </summary>
public sealed class ShiftReportContents(
    ICallerAccess caller,
    ITenantContext tenant,
    ITenantDirectory tenants,
    ISiteDirectory sites,
    IShiftEntriesOfShift entries,
    IReviewOutcomes reviews,
    IUserNames names,
    IAssetDirectory assets,
    IEnvelopeImages images)
{
    public static readonly Error NotFound = Error.NotFound("documents.shift_not_found", "No such site among yours.");

    public static readonly Error TooLong = Error.Validation(
        "documents.shift_too_long", $"A shift report holds at most {IShiftEntriesOfShift.MaxEntries} entries.");

    public static readonly Error ShiftInvalid = Error.Validation("documents.shift_invalid", "Give the shift's code (1 to 20 characters).");

    public async Task<Result<ShiftReportContent>> OfShiftAsync(Guid siteId, DateOnly date, string? shift, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(shift) || shift.Length > 20)
        {
            return ShiftInvalid;
        }

        var scope = caller.Current.ScopeOf(DocumentsPermissions.ShiftReportsRead);
        if (!scope.TenantWide && !scope.Sites.Contains(siteId))
        {
            return NotFound; // another site is not yours: the same answer as an unknown one
        }

        var company = tenant.Current ?? throw new InvalidOperationException("A report belongs to a company.");
        if (await sites.FindAsync(siteId, cancellationToken).ConfigureAwait(false) is not { } site
            || await tenants.FindAsync(company, cancellationToken).ConfigureAwait(false) is not { } summary)
        {
            return NotFound;
        }

        var found = await entries.OfShiftAsync(siteId, date, shift, cancellationToken).ConfigureAwait(false);
        if (found.Count > IShiftEntriesOfShift.MaxEntries)
        {
            return TooLong;
        }

        var rows = await RowsAsync(siteId, found, cancellationToken).ConfigureAwait(false);
        return Compose(caller.Current, ShiftReportColumns.All, rows, summary.Name, site, date, shift);
    }

    /// <summary>The masked table (TC-P2-DOC-003): the columns the reader may see, and each cell or its mask.</summary>
    public static ShiftReportContent Compose(
        CallerAccess reader, IReadOnlyList<ExportColumn<ShiftReportRow>> columns, IReadOnlyList<ShiftReportRow> rows,
        string company, SiteSummary site, DateOnly date, string shift)
    {
        ArgumentNullException.ThrowIfNull(site);
        var shown = SensitiveExport.Columns(reader, columns, rows);
        return new ShiftReportContent(
            company, site.Code, site.Name, date, shift, [.. shown.Select(c => c.Header)],
            [.. rows.Select(row => (IReadOnlyList<string>)[.. shown.Select(c => SensitiveExport.Cell(reader, c, row)?.ToString() ?? string.Empty)])]);
    }

    private async Task<IReadOnlyList<ShiftReportRow>> RowsAsync(Guid siteId, IReadOnlyList<ShiftReportEntry> found, CancellationToken cancellationToken)
    {
        var outcomes = (await reviews.OfRecordsAsync([.. found.Select(e => e.RootId)], cancellationToken).ConfigureAwait(false))
            .ToDictionary(o => o.RecordId);
        var people = await names.ByIdAsync([.. found.Select(e => e.OperatorId).Distinct()], cancellationToken).ConfigureAwait(false);
        var deciders = await names.BySubjectAsync([.. outcomes.Values.Select(o => o.DecidedBy).OfType<string>().Distinct(StringComparer.Ordinal)], cancellationToken)
            .ConfigureAwait(false);
        var signatures = await images.ImageHashesAsync([.. found.Select(e => e.EnvelopeId).Distinct()], cancellationToken).ConfigureAwait(false);
        var machines = new Dictionary<Guid, string>();
        foreach (var asset in found.Select(e => e.AssetId).Distinct())
        {
            machines[asset] = (await assets.FindAsync(asset, cancellationToken).ConfigureAwait(false))?.Code ?? string.Empty;
        }

        return
        [
            .. found.Select(e =>
            {
                var outcome = outcomes.GetValueOrDefault(e.RootId);
                return new ShiftReportRow(
                    e.RootId,
                    siteId,
                    machines.GetValueOrDefault(e.AssetId, string.Empty),
                    people.GetValueOrDefault(e.OperatorId, string.Empty),
                    Number(e.StartValue),
                    e.EndValue is { } end ? Number(end) : string.Empty,
                    e.EndValue is { } stop ? Number(stop - e.StartValue) : string.Empty,
                    e.MeterKind,
                    e.Status,
                    outcome?.Status ?? "not_reviewed",
                    outcome?.DecidedBy is { } subject ? deciders.GetValueOrDefault(subject, subject) : string.Empty,
                    string.Join(' ', e.Flags),
                    signatures.GetValueOrDefault(e.EnvelopeId, string.Empty));
            }),
        ];
    }

    /// <summary>A meter value as the content keeps it: invariant, one decimal at least (the PDF writes it in the reader's locale).</summary>
    private static string Number(decimal value) => value.ToString("0.0##", CultureInfo.InvariantCulture);
}
