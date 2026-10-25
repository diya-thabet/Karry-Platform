using System.Security.Claims;
using FluentValidation;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Tenancy.Application;
using Karry.Modules.Tenancy.Domain;
using Karry.Modules.Tenancy.Infrastructure;
using Karry.SharedKernel.Tenancy;
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
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("ProvisionTenant");

        // The caller's own company (portal header). Tenant-scoped: the tenant comes from the token only.
        // Name, slug and branding of the caller's own company: every member needs it for the app header.
        endpoints.MapGet("/current", CurrentAsync)
            .AccountAccess()
            .AllowAnyTenantMember()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetCurrentTenant");

        // Login page branding before sign-in (name now; logo and colour with tenant settings, P1.11). The Keycloak
        // login page reads it from another origin (deploy/keycloak/themes/karry): public data, CORS for any origin.
        endpoints.MapGet("/public/{slug}", BrandingAsync)
            .AllowAnonymous()
            .AllowWithoutTenant()
            .AllowPublicCrossOrigin()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetTenantBranding");
    }

    private static async Task<Results<Created<ProvisionedTenantResponse>, Ok<ProvisionedTenantResponse>, ProblemHttpResult>> ProvisionAsync(
        ProvisionTenant request, ClaimsPrincipal caller, [FromServices] ProvisionTenantHandler handler, [FromServices] IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        var tenant = result.Value;
        var invitation = request.Admin is null ? null : await InviteFirstAdminAsync(tenant.Id, request.Admin, caller, scopes, cancellationToken).ConfigureAwait(false);
        var body = new ProvisionedTenantResponse(tenant.Id, tenant.Slug, tenant.Name, tenant.CreatedAt, invitation);
        return tenant.Created
            ? TypedResults.Created($"/api/v1/tenancy/tenants/{tenant.Id}", body)
            : TypedResults.Ok(body);
    }

    /// <summary>
    /// In its own scope: the invitation works under the new tenant, while this request stays a platform request. The
    /// tenant exists whatever happens here; the operator repeats the (idempotent) provisioning to retry the invitation.
    /// </summary>
    private static async Task<AdminInvitationResponse> InviteFirstAdminAsync(
        Guid tenant, FirstAdmin admin, ClaimsPrincipal caller, IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IFirstAdminInvitation>() is not { } invitations)
        {
            return new AdminInvitationResponse(Invited: false, Delivered: false, Error: "identity.unavailable");
        }

        var invited = await invitations.InviteAsync(new TenantId(tenant), admin.FirstName, admin.LastName, admin.Email,
            caller.FindFirst("sub")?.Value, cancellationToken).ConfigureAwait(false);
        return invited.IsSuccess
            ? new AdminInvitationResponse(invited.Value.Invited, invited.Value.Delivered, Error: null)
            : new AdminInvitationResponse(Invited: false, Delivered: false, invited.Error.Code);
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

/// <summary>The provisioned tenant; <c>AdminInvitation</c> is the outcome of the first administrator invitation when one was requested.</summary>
public sealed record ProvisionedTenantResponse(Guid Id, string Slug, string Name, DateTimeOffset CreatedAt, AdminInvitationResponse? AdminInvitation);

/// <summary><c>Invited</c> is false when the tenant already had an administrator, or on <c>Error</c>.</summary>
public sealed record AdminInvitationResponse(bool Invited, bool Delivered, string? Error);

public sealed record TenantBrandingResponse(string Slug, string Name);
