using System.Diagnostics.CodeAnalysis;

namespace Karry.SharedKernel.Results;

/// <summary>
/// Outcome of an operation that can fail for expected reasons. Exceptions stay for bugs and infrastructure
/// failures; expected failures (validation, not found, business rules) are values.
/// </summary>
public readonly record struct Result<T>
{
    private readonly T? value;

    internal Result(T value)
    {
        this.value = value;
        Error = null;
    }

    internal Result(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        value = default;
        Error = error;
    }

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    /// <summary>The value; throws when the result is a failure (check <see cref="IsSuccess"/> first).</summary>
    public T Value => IsSuccess ? value! : throw new InvalidOperationException($"Result is a failure ({Error.Code}); it has no value.");

    public static implicit operator Result<T>(T value) => new(value);

    public static implicit operator Result<T>(Error error) => new(error);

    public Result<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? new Result<TOut>(map(value!)) : new Result<TOut>(Error);
    }

    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? bind(value!) : new Result<TOut>(Error);
    }
}

/// <summary>Factory methods for <see cref="Result{T}"/> (implicit conversions from a value or an <see cref="Error"/> also work).</summary>
public static class Result
{
    public static Result<T> Success<T>(T value) => new(value);

    public static Result<T> Failure<T>(Error error) => new(error);
}
