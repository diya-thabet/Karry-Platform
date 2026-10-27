using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Imports;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Imports.Application;

/// <summary>
/// Imports permissions (ADR-0027). Loading a company's data from files is an onboarding task of the Tenant Admin; each
/// kind also needs the permission of the data it writes (the target's).
/// </summary>
public static class ImportsPermissions
{
    /// <summary>Download templates, upload files, read their reports, commit and discard them.</summary>
    public const string FilesManage = "imports.files.manage";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(FilesManage, ScopeKind.Tenant, privileged: true, Roles.TenantAdmin),
    ];
}

/// <summary>The rows of a file and the problems found while reading it (columns, formulas, cell errors).</summary>
public sealed record WorkbookContent(IReadOnlyList<ImportRow> Rows, IReadOnlyList<ImportIssue> Issues);

/// <summary>Reads an uploaded workbook after checking it is a plain <c>.xlsx</c> within the limits (ADR-0027 §6).</summary>
public interface IWorkbookReader
{
    Result<WorkbookContent> Read(byte[] file, IReadOnlyList<ImportColumn> columns);
}

/// <summary>Writes a kind's template (ADR-0027 §8): data sheet, instructions and hidden drop-down lists.</summary>
public interface ITemplateWriter
{
    byte[] Write(IReadOnlyList<ImportColumn> columns, string language, IReadOnlyDictionary<string, IReadOnlyList<string>> lists);
}

/// <summary>Errors of import files and kinds (problem codes the apps translate).</summary>
public static class ImportErrors
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxRows = 5_000;
    public const int MaxColumns = 60;

    public static readonly Error KindUnknown = Error.NotFound("imports.kind_unknown", "There is no import of this kind.");

    public static readonly Error LanguageUnsupported = Error.Validation("imports.language_unsupported", "Templates are in French (fr) or English (en).");

    public static readonly Error TargetForbidden = Error.Forbidden("imports.kind_forbidden", "You may not change the data this import writes.");

    public static readonly Error FileTypeUnsupported =
        Error.Validation("imports.file_type_unsupported", "Upload an Excel workbook (.xlsx). Older .xls files, CSV and macro-enabled workbooks are not accepted.");

    public static readonly Error FileTooLarge = Error.Validation("imports.file_too_large", $"A file is at most {MaxBytes / (1024 * 1024)} MB.");

    public static readonly Error FileActiveContent =
        Error.Validation("imports.file_active_content", "The workbook contains macros, ActiveX controls, embedded objects or external links; save a plain copy.");

    public static readonly Error FileTooManyRows =
        Error.Validation("imports.file_too_many_rows", $"A file has at most {MaxRows} data rows and {MaxColumns} columns; split it.");

    public static readonly Error FileUnreadable = Error.Validation("imports.file_unreadable", "The workbook cannot be read; open it in Excel and save it again.");
}

/// <summary>The import kinds the modules offer, found through dependency injection (ADR-0027 §2).</summary>
public sealed class ImportTargets(IEnumerable<IImportTarget> targets)
{
    private readonly Dictionary<string, IImportTarget> byKind = targets.ToDictionary(t => t.Kind, StringComparer.Ordinal);

    public IReadOnlyList<string> Kinds => [.. byKind.Keys.Order(StringComparer.Ordinal)];

    public IImportTarget? Find(string? kind) => kind is not null && byKind.TryGetValue(kind, out var target) ? target : null;
}

/// <summary>Templates of the import kinds (US-P1-ORG-03 AC1), in French or English, with the company's own lists.</summary>
public sealed class ImportTemplates(ImportTargets targets, ITemplateWriter writer, ICallerAccess caller)
{
    public static readonly string[] Languages = ["fr", "en"];

    public async Task<Result<byte[]>> TemplateAsync(string kind, string? language, CancellationToken cancellationToken)
    {
        var target = targets.Find(kind);
        if (target is null)
        {
            return ImportErrors.KindUnknown;
        }

        var chosen = language ?? "fr";
        if (!Languages.Contains(chosen, StringComparer.Ordinal))
        {
            return ImportErrors.LanguageUnsupported;
        }

        if (!caller.Current.Has(target.Permission))
        {
            return ImportErrors.TargetForbidden;
        }

        var lists = await target.ListsAsync(cancellationToken).ConfigureAwait(false);
        return writer.Write(target.Columns, chosen, lists);
    }
}
