using Karry.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Karry.BuildingBlocks.Api;

/// <summary>
/// RFC 9457 problem details conventions (docs/02-architecture/api-standards.md). Every error response carries:
/// <c>type</c> (stable URI per error code), <c>title</c>, <c>status</c>, <c>code</c> (stable machine code),
/// <c>traceId</c> (for support) and, for validation errors, <c>errors</c> per field.
/// </summary>
public static class ProblemTypes
{
    public const string BaseUri = "https://karry.app/problems/";

    /// <summary>Code of the generic 500 response; details stay in logs and traces.</summary>
    public const string UnexpectedCode = "server.unexpected_error";

    /// <summary>Code of request validation failures (malformed or invalid input).</summary>
    public const string ValidationCode = "request.invalid";

    public static Uri For(string code) => new(BaseUri + code);

    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.BusinessRule => StatusCodes.Status422UnprocessableEntity,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped error kind."),
    };
}
