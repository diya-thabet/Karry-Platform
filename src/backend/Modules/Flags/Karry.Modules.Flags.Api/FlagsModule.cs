using System.Security.Claims;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Flags;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Flags.Application;
using Karry.Modules.Flags.Domain;
using Karry.Modules.Flags.Infrastructure;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Flags;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Flags.Api;

/// <summary>
/// Feature flags (US-P1-PLT-05, ADR-0019). Routes under <c>/api/v1/flags</c>: the caller's evaluated flags for the apps,
/// and platform-operator changes (platform-wide, per company, per user) that apply without a release.
/// </summary>
public sealed class FlagsModule : IModule
{
    public string Name => "flags";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map ([FromServices]) and fail only when called.
            return;
        }

        services.AddFlagsInfrastructure(connectionString);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The caller's flags, evaluated through OpenFeature, for the apps' web provider. Readable in every subscription
        // state: the apps need it to start.
        endpoints.MapGet("", CurrentFlagsAsync)
            .AllowAnyTenantMember()
            .AccountAccess()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("GetCurrentFlags");

        var operators = endpoints.MapGroup("")
            .AllowWithoutTenant()
            .RequireAuthorization(KarryPolicies.PlatformOperator)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        operators.MapGet("/catalog", CatalogAsync).WithName("ListFlags");

        // The platform-wide value in this environment (the kill switch); enabled null removes it.
        operators.MapPut("/{key}", SetPlatformAsync)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("SetPlatformFlag");

        operators.MapGet("/tenants/{tenantId:guid}", TenantOverridesAsync)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ListTenantFlags");

        // A company's value, or one user's (userSubject); enabled null removes it. Ledgered in that company's chain.
        operators.MapPut("/tenants/{tenantId:guid}/{key}", SetTenantAsync)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("SetTenantFlag");
    }

    private static async Task<Ok<CurrentFlagsResponse>> CurrentFlagsAsync([FromServices] FeatureFlagClient flags, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var flag in FeatureFlags.All)
        {
            values[flag.Key] = await flags.IsEnabledAsync(flag, cancellationToken).ConfigureAwait(false);
        }

        return TypedResults.Ok(new CurrentFlagsResponse(values));
    }

    private static async Task<Ok<FlagCatalogResponse>> CatalogAsync([FromServices] IFlagStore store, [FromServices] FlagOperations operations, CancellationToken cancellationToken)
    {
        var platform = (await store.PlatformAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(f => f.Key, StringComparer.Ordinal);
        return TypedResults.Ok(new FlagCatalogResponse([.. FeatureFlags.All.Select(f => new FlagResponse(
            f.Key, f.Description, f.Default, operations.EnvironmentValue(f), platform.GetValueOrDefault(f.Key)?.Value, platform.GetValueOrDefault(f.Key)?.ChangedAt))]));
    }

    private static async Task<Results<Ok<FlagResponse>, ProblemHttpResult>> SetPlatformAsync(
        string key, SetFlag request, ClaimsPrincipal caller, [FromServices] FlagOperations operations, CancellationToken cancellationToken)
    {
        var result = await operations.SetPlatformAsync(key, request, Actor(caller), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        var flag = FeatureFlags.Find(key)!;
        return TypedResults.Ok(new FlagResponse(flag.Key, flag.Description, flag.Default, operations.EnvironmentValue(flag), result.Value.Value, result.Value.ChangedAt));
    }

    private static Task<Results<Ok<TenantFlagListResponse>, ProblemHttpResult>> TenantOverridesAsync(
        Guid tenantId, [FromServices] IServiceScopeFactory scopes, CancellationToken cancellationToken) =>
        InTenantAsync(scopes, tenantId, async (services, _) =>
            Result.Success(TenantFlagListResponse.Of(await services.GetRequiredService<IFlagStore>().TenantAsync(cancellationToken).ConfigureAwait(false))),
            cancellationToken);

    private static Task<Results<Ok<TenantFlagResponse>, ProblemHttpResult>> SetTenantAsync(
        Guid tenantId, string key, SetFlag request, ClaimsPrincipal caller, [FromServices] IServiceScopeFactory scopes, CancellationToken cancellationToken) =>
        InTenantAsync(scopes, tenantId, async (services, tenant) =>
        {
            var result = await services.GetRequiredService<FlagOperations>().SetTenantAsync(tenant, key, request, Actor(caller), cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? Result.Success(TenantFlagResponse.Of(result.Value)) : result.Error;
        }, cancellationToken);

    /// <summary>An operator request for one tenant, in its own scope (its RLS context and ledger); an unknown tenant is 404.</summary>
    private static async Task<Results<Ok<TResponse>, ProblemHttpResult>> InTenantAsync<TResponse>(IServiceScopeFactory scopes, Guid tenantId,
        Func<IServiceProvider, TenantId, Task<Result<TResponse>>> work, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenant = new TenantId(tenantId);
        if (tenantId == Guid.Empty
            || await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindAsync(tenant, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound("flags.tenant_not_found", "No company has this ID.").ToProblem();
        }

        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
        var result = await work(scope.ServiceProvider, tenant).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static string? Actor(ClaimsPrincipal caller) => caller.FindFirst("sub")?.Value;
}

/// <summary>The caller's flags: key → enabled.</summary>
public sealed record CurrentFlagsResponse(IReadOnlyDictionary<string, bool> Flags);

public sealed record FlagCatalogResponse(IReadOnlyList<FlagResponse> Items);

/// <summary>
/// A declared flag: its default, this environment's configured value and the platform-wide value (null when not set).
/// Company and user overrides come before these.
/// </summary>
public sealed record FlagResponse(string Key, string Description, bool Default, bool? Environment, bool? Platform, DateTimeOffset? PlatformChangedAt);

public sealed record TenantFlagListResponse(IReadOnlyList<TenantFlagResponse> Items)
{
    public static TenantFlagListResponse Of(IReadOnlyList<TenantFlag> flags) => new([.. flags.Where(f => f.Value is not null).Select(TenantFlagResponse.Of)]);
}

/// <summary>A company override (<c>UserSubject</c> null) or a user's; <c>Enabled</c> null: removed.</summary>
public sealed record TenantFlagResponse(string Key, string? UserSubject, bool? Enabled, DateTimeOffset ChangedAt)
{
    public static TenantFlagResponse Of(TenantFlag flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        return new(flag.Key, flag.Subject.Length == 0 ? null : flag.Subject, flag.Value, flag.ChangedAt);
    }
}
