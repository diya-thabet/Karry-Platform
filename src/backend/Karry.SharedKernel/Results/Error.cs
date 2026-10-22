namespace Karry.SharedKernel.Results;

/// <summary>Kinds of expected failures; the API maps each to an HTTP status (RFC 9457 problem details).</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    BusinessRule,
}

/// <summary>
/// An expected failure with a stable, documented code (e.g. <c>asset.not_found</c>). Codes are part of the API
/// contract: clients and translations rely on them, so never change an existing one.
/// </summary>
public sealed record Error
{
    private Error(ErrorKind kind, string code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Kind = kind;
        Code = code;
        Message = message;
        FieldErrors = fieldErrors ?? new Dictionary<string, string[]>();
    }

    public ErrorKind Kind { get; }

    public string Code { get; }

    public string Message { get; }

    /// <summary>Per-field messages for <see cref="ErrorKind.Validation"/> errors.</summary>
    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }

    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        new(ErrorKind.Validation, code, message, fieldErrors);

    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message, null);

    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message, null);

    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message, null);

    /// <summary>A domain rule refused the operation (e.g. "hour meters never go backwards").</summary>
    public static Error BusinessRule(string code, string message) => new(ErrorKind.BusinessRule, code, message, null);
}
