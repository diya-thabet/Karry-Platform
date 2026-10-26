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
using Karry.SharedKernel.Authorization;
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

/// <summary>Tenant catalog, provisioning and company settings (US-P1-TEN-01, TEN-04). Routes under <c>/api/v1/tenancy</c>.</summary>
public sealed class TenancyModule : IModule
{
    public string Name => "tenancy";

    public IReadOnlyList<PermissionDefinition> Permissions => TenancyPermissions.All;

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

        // Login page branding before sign-in: name, colour and logo address. The Keycloak login page reads it from
        // another origin (deploy/keycloak/themes/karry): public data, CORS for any origin.
        endpoints.MapGet("/public/{slug}", BrandingAsync)
            .AllowAnonymous()
            .AllowWithoutTenant()
            .AllowPublicCrossOrigin()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetTenantBranding");

        endpoints.MapGet("/public/{slug}/logo", LogoAsync)
            .AllowAnonymous()
            .AllowWithoutTenant()
            .AllowPublicCrossOrigin()
            .Produces(StatusCodes.Status200OK, contentType: "image/png", additionalContentTypes: ["image/jpeg", "image/webp"])
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetTenantLogo");

        // The company's settings (US-P1-TEN-04): a full replacement, audited, seen by every member at their next read.
        endpoints.MapPut("/settings", UpdateSettingsAsync)
            .RequirePermission(TenancyPermissions.SettingsManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("UpdateTenantSettings");

        // The logo as the request body (PNG, JPEG or WebP, at most 256 KB); its type is read from the content.
        endpoints.MapPut("/settings/logo", SetLogoAsync)
            .RequirePermission(TenancyPermissions.SettingsManage)
            .Accepts<IFormFile>("image/png", "image/jpeg", "image/webp")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("SetTenantLogo");

        endpoints.MapDelete("/settings/logo", RemoveLogoAsync)
            .RequirePermission(TenancyPermissions.SettingsManage)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("RemoveTenantLogo");
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
        var current = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        return current is null
            ? SharedKernel.Results.Error.NotFound("tenant.not_found", "The company of this account does not exist.").ToProblem()
            : TypedResults.Ok(TenantResponse.From(current));
    }

    private static async Task<Results<Ok<TenantResponse>, ProblemHttpResult>> UpdateSettingsAsync(
        UpdateTenantSettings request, ClaimsPrincipal caller, [FromServices] ITenantContext tenant, [FromServices] TenantSettingsOperations settings,
        CancellationToken cancellationToken)
    {
        var result = await settings.UpdateAsync(Own(tenant), request, caller.FindFirst("sub")?.Value, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(TenantResponse.From(result.Value)) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<TenantResponse>, ProblemHttpResult>> SetLogoAsync(
        HttpRequest request, ClaimsPrincipal caller, [FromServices] ITenantContext tenant, [FromServices] TenantSettingsOperations settings,
        CancellationToken cancellationToken)
    {
        // One byte more than allowed is enough to refuse; a larger body is never read into memory.
        var buffer = new byte[TenantLogo.MaxBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            length += read;
        }

        var result = await settings.SetLogoAsync(Own(tenant), buffer.AsMemory(0, length), caller.FindFirst("sub")?.Value, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(TenantResponse.From(result.Value)) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<TenantResponse>, ProblemHttpResult>> RemoveLogoAsync(
        ClaimsPrincipal caller, [FromServices] ITenantContext tenant, [FromServices] TenantSettingsOperations settings, CancellationToken cancellationToken)
    {
        var result = await settings.RemoveLogoAsync(Own(tenant), caller.FindFirst("sub")?.Value, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(TenantResponse.From(result.Value)) : result.Error.ToProblem();
    }

    private static async Task<IResult> LogoAsync(string slug, HttpContext context, [FromServices] TenancyDbContext db, CancellationToken cancellationToken)
    {
        var valid = TenantSlug.Create(slug);
        var tenant = valid.IsSuccess
            ? await db.Tenants.AsNoTracking().Where(t => t.Slug == valid.Value).Select(t => (TenantId?)t.Id)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var logo = tenant is { } id
            ? await db.Logos.AsNoTracking().SingleOrDefaultAsync(l => l.TenantId == id.Value, cancellationToken).ConfigureAwait(false)
            : null;
        if (logo is null)
        {
            return SharedKernel.Results.Error.NotFound("tenant.logo_not_found", "This company has no logo.").ToProblem();
        }

        // Served as an image only: never sniffed into something else, no active content even if opened directly.
        context.Response.Headers.CacheControl = "public, max-age=300";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.File(logo.Content, logo.ContentType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{logo.Sha256}\""));
    }

    private static TenantId Own(ITenantContext tenant) =>
        tenant.Current ?? throw new InvalidOperationException("Tenant-scoped endpoint reached without a tenant.");

    private static async Task<Results<Ok<TenantBrandingResponse>, ProblemHttpResult>> BrandingAsync(
        string slug, [FromServices] TenancyDbContext db, CancellationToken cancellationToken)
    {
        var valid = TenantSlug.Create(slug);
        var tenant = valid.IsSuccess
            ? await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == valid.Value, cancellationToken).ConfigureAwait(false)
            : null;
        return tenant is null
            ? SharedKernel.Results.Error.NotFound("tenant.not_found", "No company uses this address.").ToProblem()
            : TypedResults.Ok(new TenantBrandingResponse(tenant.Slug.Value, tenant.Name, tenant.PrimaryColour, TenantResponse.LogoAddress(tenant)));
    }
}

/// <summary>The caller's company with its settings (US-P1-TEN-04); <c>LogoUrl</c> is null without a logo.</summary>
public sealed record TenantResponse(
    Guid Id, string Slug, string Name, DateTimeOffset CreatedAt, string LegalName, string? PrimaryColour, string Language, string Currency,
    string UnitSystem, string TimeZone, int FiscalYearStartMonth, string? LogoUrl, DateTimeOffset? SettingsChangedAt)
{
    public static TenantResponse From(Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return new(tenant.Id.Value, tenant.Slug.Value, tenant.Name, tenant.CreatedAt, tenant.LegalName, tenant.PrimaryColour, tenant.Language,
            tenant.Currency, tenant.UnitSystem.ToString().ToLowerInvariant(), tenant.TimeZone, tenant.FiscalYearStartMonth, LogoAddress(tenant),
            tenant.SettingsChangedAt);
    }

    /// <summary>Public address of the logo; the fingerprint in it changes with the image, so caches never show an old one.</summary>
    public static string? LogoAddress(Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return tenant.LogoSha256 is { } sha ? $"/api/v1/tenancy/public/{tenant.Slug.Value}/logo?v={sha[..12]}" : null;
    }
}

/// <summary>The provisioned tenant; <c>AdminInvitation</c> is the outcome of the first administrator invitation when one was requested.</summary>
public sealed record ProvisionedTenantResponse(Guid Id, string Slug, string Name, DateTimeOffset CreatedAt, AdminInvitationResponse? AdminInvitation);

/// <summary><c>Invited</c> is false when the tenant already had an administrator, or on <c>Error</c>.</summary>
public sealed record AdminInvitationResponse(bool Invited, bool Delivered, string? Error);

/// <summary>Public sign-in branding: no data beyond what the sign-in page shows.</summary>
public sealed record TenantBrandingResponse(string Slug, string Name, string? PrimaryColour, string? LogoUrl);
