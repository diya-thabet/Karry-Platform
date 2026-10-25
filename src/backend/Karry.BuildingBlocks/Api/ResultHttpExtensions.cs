using System.Diagnostics;
using Karry.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Karry.BuildingBlocks.Api;

/// <summary>Turns a <see cref="Result{T}"/> into an HTTP response; failures become problem details.</summary>
public static class ResultHttpExtensions
{
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult>? onSuccess = null) =>
        result.IsSuccess ? (onSuccess ?? (value => TypedResults.Ok(value)))(result.Value) : result.Error.ToProblem();

    public static ProblemHttpResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = error.Code,
            ["traceId"] = Activity.Current?.TraceId.ToString(),
        };
        if (error.FieldErrors.Count > 0)
        {
            extensions["errors"] = error.FieldErrors;
        }

        foreach (var (name, value) in error.Extensions)
        {
            extensions[name] = value;
        }

        return TypedResults.Problem(
            title: error.Message,
            statusCode: ProblemTypes.StatusFor(error.Kind),
            type: ProblemTypes.For(error.Code).ToString(),
            extensions: extensions);
    }
}
