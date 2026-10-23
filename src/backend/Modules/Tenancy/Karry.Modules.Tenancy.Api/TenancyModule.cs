using FluentValidation;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Tenancy.Application;
using Karry.Modules.Tenancy.Domain;
using Karry.Modules.Tenancy.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Tenancy.Api;

/// <summary>Tenant catalog and provisioning (US-P1-TEN-01). Routes under <c>/api/v1/tenancy</c>.</summary>
public sealed class TenancyModule : IModule
{
    public string Name => "tenancy";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map ([FromServices]) and fail only when called;
            // /health/ready reports the missing database.
            return;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddTenancyInfrastructure(connectionString);
        services.AddScoped<ProvisionTenantHandler>();
        services.AddScoped<IValidator<ProvisionTenant>, ProvisionTenantValidator>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Platform operators provision tenants: no tenant in their token; idempotent by key and by slug.
        endpoints.MapPost("/tenants", ProvisionAsync)
            .AllowWithoutTenant()
            .RequireAuthorization(KarryPolicies.PlatformOperator)
            .Validate<ProvisionTenant>()
            .RequireIdempotencyKey()
            .WithName("ProvisionTenant");

        // The caller's own company (portal header). Tenant-scoped: the tenant comes from the token only.
        endpoints.MapGet("/current", CurrentAsync).WithName("GetCurrentTenant");

        // Login page branding before sign-in (name now; logo and colour with tenant settings, P1.11).
        endpoints.MapGet("/public/{slug}", BrandingAsync)
            .AllowAnonymous()
            .AllowWithoutTenant()
            .WithName("GetTenantBranding");
    }

    private static async Task<Results<Created<TenantResponse>, Ok<TenantResponse>, ProblemHttpResult>> ProvisionAsync(
        ProvisionTenant request, [FromServices] ProvisionTenantHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        var tenant = result.Value;
        var body = new TenantResponse(tenant.Id, tenant.Slug, tenant.Name, tenant.CreatedAt);
        return tenant.Created
            ? TypedResults.Created($"/api/v1/tenancy/tenants/{tenant.Id}", body)
            : TypedResults.Ok(body);
    }

    private static async Task<Results<Ok<TenantResponse>, ProblemHttpResult>> CurrentAsync(
        [FromServices] ITenantContext tenant, [FromServices] TenancyDbContext db, CancellationToken cancellationToken)
    {
        var id = tenant.Current ?? throw new InvalidOperationException("Tenant-scoped endpoint reached without a tenant.");
        var current = await db.Tenants.AsNoTracking().Where(t => t.Id == id)
            .Select(t => new TenantResponse(t.Id.Value, t.Slug.Value, t.Name, t.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return current is null
            ? SharedKernel.Results.Error.NotFound("tenant.not_found", "The company of this account does not exist.").ToProblem()
            : TypedResults.Ok(current);
    }

    private static async Task<Results<Ok<TenantBrandingResponse>, ProblemHttpResult>> BrandingAsync(
        string slug, [FromServices] TenancyDbContext db, CancellationToken cancellationToken)
    {
        var valid = TenantSlug.Create(slug);
        var tenant = valid.IsSuccess
            ? await db.Tenants.AsNoTracking().Where(t => t.Slug == valid.Value)
                .Select(t => new TenantBrandingResponse(t.Slug.Value, t.Name)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        return tenant is null
            ? SharedKernel.Results.Error.NotFound("tenant.not_found", "No company uses this address.").ToProblem()
            : TypedResults.Ok(tenant);
    }
}

public sealed record TenantResponse(Guid Id, string Slug, string Name, DateTimeOffset CreatedAt);

public sealed record TenantBrandingResponse(string Slug, string Name);
