using FluentValidation;
using Karry.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.Api;

/// <summary>
/// Validates the request body of type <typeparamref name="TRequest"/> with its registered FluentValidation
/// validator before the handler runs; invalid input returns 400 <c>request.invalid</c> with per-field errors.
/// </summary>
public sealed class ValidationFilter<TRequest> : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var validator = context.HttpContext.RequestServices.GetService<IValidator<TRequest>>()
            ?? throw new InvalidOperationException($"No IValidator<{typeof(TRequest).Name}> registered; every request type needs one.");
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
        if (request is null)
        {
            return Error.Validation(ProblemTypes.ValidationCode, "The request body is missing.").ToProblem();
        }

        var validation = await validator.ValidateAsync(request, context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (validation.IsValid)
        {
            return await next(context).ConfigureAwait(false);
        }

        var fieldErrors = validation.Errors
            .GroupBy(e => e.PropertyName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray(), StringComparer.Ordinal);
        return Error.Validation(ProblemTypes.ValidationCode, "The request is invalid.", fieldErrors).ToProblem();
    }
}

public static class ValidationFilterExtensions
{
    /// <summary>Adds request validation to an endpoint: <c>group.MapPost("/", handler).Validate&lt;CreateAsset&gt;()</c>.</summary>
    public static RouteHandlerBuilder Validate<TRequest>(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<ValidationFilter<TRequest>>().ProducesValidationProblem();
}
