using System.Globalization;

namespace Karry.BuildingBlocks.Imports;

/// <summary>How a template formats a column: text (so Excel never turns a code such as <c>1/2</c> into a date), number or date.</summary>
public enum ImportColumnType
{
    Text = 1,
    Number = 2,
    Date = 3,
}

/// <summary>
/// A column of an import template (ADR-0027): its stable key, its headers in English and French (either is accepted when
/// reading, in any order), whether a value is required, its type, the list its drop-down uses and an example value.
/// </summary>
public sealed record ImportColumn(
    string Key, string English, string French, bool Required, ImportColumnType Type = ImportColumnType.Text, string? List = null, string? Example = null)
{
    public string HeaderIn(string language) => language == "fr" ? French : English;
}

/// <summary>A cell as read: its reference (<c>C14</c>) and its value as text (numbers invariant, dates ISO), null when empty.</summary>
public sealed record ImportCell(string Reference, string? Text);

/// <summary>A data row of a file: its row number in the sheet and its cells by column key (missing optional columns are absent).</summary>
public sealed record ImportRow(int Number, IReadOnlyDictionary<string, ImportCell> Cells)
{
    public string? this[string column] => Cells.TryGetValue(column, out var cell) ? cell.Text : null;

    /// <summary>The cell reference of a column on this row, also for an empty cell (for issues).</summary>
    public string? Reference(string column) => Cells.TryGetValue(column, out var cell) ? cell.Reference : null;
}

public enum ImportSeverity
{
    Error = 1,
    Warning = 2,
}

/// <summary>
/// A problem found in a file (US-P1-ORG-03 AC2): the row (0 for the file itself), the column key and cell when known, a
/// problem code the apps translate, and its arguments. Any error blocks the commit; warnings do not.
/// </summary>
public sealed record ImportIssue(int Row, string? Column, string? Cell, string Code, ImportSeverity Severity, IReadOnlyDictionary<string, string>? Args = null)
{
    public static ImportIssue Error(ImportRow row, string column, string code, IReadOnlyDictionary<string, string>? args = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(row.Number, column, row.Reference(column), code, ImportSeverity.Error, args);
    }

    public static ImportIssue Warning(ImportRow row, string column, string code, IReadOnlyDictionary<string, string>? args = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(row.Number, column, row.Reference(column), code, ImportSeverity.Warning, args);
    }
}

/// <summary>What a commit would do with a row (the preview): create, update or leave it as it is.</summary>
public enum ImportAction
{
    Create = 1,
    Update = 2,
    Unchanged = 3,
}

public sealed record ImportRowPlan(int Row, string Key, ImportAction Action);

/// <summary>The issues of a file and, for each row without an error, what a commit would do.</summary>
public sealed record ImportValidation(IReadOnlyList<ImportIssue> Issues, IReadOnlyList<ImportRowPlan> Plans)
{
    public bool HasErrors => Issues.Any(issue => issue.Severity == ImportSeverity.Error);
}

/// <summary>
/// A commit: the validation made inside the transaction and, when it had no error, what was written. With an error,
/// nothing was written (all or nothing per file, US-P1-ORG-03 AC3).
/// </summary>
public sealed record ImportCommit(ImportValidation Validation, int Created, int Updated, int Unchanged)
{
    public bool Committed => !Validation.HasErrors;
}

/// <summary>
/// One kind of import (ADR-0027), implemented by the module that owns the data with its own operations, so imported
/// rows follow the same rules and reach the ledger the same way as through the API. Runs in a scope where the tenant
/// and the requester's access are set.
/// </summary>
public interface IImportTarget
{
    /// <summary><c>sites</c>, <c>assets</c>, <c>meters</c>, <c>materials</c> or <c>users</c>.</summary>
    string Kind { get; }

    /// <summary>The permission the requester needs besides <c>imports.files.manage</c>.</summary>
    string Permission { get; }

    IReadOnlyList<ImportColumn> Columns { get; }

    /// <summary>The values of the template's drop-down lists, by list name (e.g. the company's site codes).</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListsAsync(CancellationToken cancellationToken);

    /// <summary>Checks every row without writing anything; also reports keys that appear twice.</summary>
    Task<ImportValidation> ValidateAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken);

    /// <summary>Validates again and writes every row in one transaction, or nothing when an error remains.</summary>
    Task<ImportCommit> CommitAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken);
}

/// <summary>Reading typed values from import cells: invariant text as the reader writes it, also a decimal comma.</summary>
public static class ImportValues
{
    public static bool TryDecimal(string? text, out decimal value) =>
        decimal.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    public static bool TryInteger(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    /// <summary>An ISO date (<c>2026-03-01</c>, as the reader writes Excel dates) or a day-first date (<c>01/03/2026</c>).</summary>
    public static bool TryDate(string? text, out DateOnly value) =>
        DateOnly.TryParseExact(text?.Trim(), ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
}
