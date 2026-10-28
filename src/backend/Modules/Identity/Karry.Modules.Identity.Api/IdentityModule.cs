using System.Security.Claims;
using FluentValidation;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;
using Karry.Modules.Identity.Infrastructure;
using Karry.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Identity.Api;

/// <summary>
/// Users, invitations and deactivation (US-P1-IAM-03), roles and field levels (US-P1-IAM-05, US-P1-IAM-07). Routes
/// under <c>/api/v1/identity</c>.
/// </summary>
public sealed class IdentityModule : IModule
{
    private const int MaxPageSize = 200;

    public string Name => "identity";

    public IReadOnlyList<PermissionDefinition> Permissions => IdentityPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map and fail only when called.
            return;
        }

        services.AddIdentityInfrastructure(configuration, connectionString);
        services.AddScoped<IValidator<InviteUser>, InviteUserValidator>();
        services.AddScoped<IValidator<CreateFieldUser>, CreateFieldUserValidator>();
        services.AddScoped<IValidator<AcceptInvitation>, AcceptInvitationValidator>();
        services.AddScoped<IValidator<ChangeUserRoles>, ChangeUserRolesValidator>();
        services.AddScoped<IValidator<CreateRole>, CreateRoleValidator>();
        services.AddScoped<IValidator<ChangeRole>, ChangeRoleValidator>();
        services.AddScoped<IValidator<ChangeFieldLevels>, ChangeFieldLevelsValidator>();
        services.AddScoped<IValidator<EnrollDevice>, EnrollDeviceValidator>();
        services.AddScoped<IValidator<UnlockOnTablet>, UnlockOnTabletValidator>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        DeviceEndpoints.Map(endpoints);

        endpoints.MapPost("/invitations", InviteAsync)
            .RequirePermission(IdentityPermissions.UsersManage)
            .Validate<InviteUser>()
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("InviteUser");

        // The invitee has no account yet: the token is the credential and names the tenant itself.
        endpoints.MapPost("/invitations/accept", AcceptAsync)
            .AllowAnonymous()
            .AllowWithoutTenant()
            .Validate<AcceptInvitation>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("AcceptInvitation");

        endpoints.MapPost("/field-users", CreateFieldUserAsync)
            .RequirePermission(IdentityPermissions.UsersManage)
            .Validate<CreateFieldUser>()
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("CreateFieldUser");

        endpoints.MapPost("/users/{id:guid}/deactivate", DeactivateAsync)
            .RequirePermission(IdentityPermissions.UsersManage)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("DeactivateUser");

        // Privileged role changes answer 401 auth.step_up_required unless the sign-in is recent (US-P1-IAM-05 AC3).
        endpoints.MapPut("/users/{id:guid}/roles", ChangeRolesAsync)
            .RequirePermission(IdentityPermissions.UsersManage)
            .AddEndpointFilter(AddressedUserVisible)
            .Validate<ChangeUserRoles>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("ChangeUserRoles");

        endpoints.MapPost("/users/{id:guid}/invitation", ResendInvitationAsync)
            .RequirePermission(IdentityPermissions.UsersManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("ResendInvitation");

        // A field user's PIN, set by an administrator (an imported field user has none yet, P1.14).
        endpoints.MapPut("/users/{id:guid}/pin", SetPinAsync)
            .RequirePermission(IdentityPermissions.UsersManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("SetUserPin");

        endpoints.MapGet("/users", ListAsync)
            .RequirePermission(IdentityPermissions.UsersRead)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("ListUsers");

        endpoints.MapGet("/users/{id:guid}", GetAsync)
            .RequirePermission(IdentityPermissions.UsersRead)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetUser");

        // The signed-in user's own profile (portal header, MFA hint): every member, about themselves only.
        endpoints.MapGet("/me", MeAsync)
            .AccountAccess()
            .AllowAnyTenantMember()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetCurrentUser");

        // The signed-in user's own permissions, so the apps show only what they can use: every member, about themselves.
        endpoints.MapGet("/me/access", CurrentAccess)
            .AccountAccess()
            .AllowAnyTenantMember()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithName("GetCurrentAccess");

        endpoints.MapGet("/permissions", PermissionCatalog)
            .RequirePermission(IdentityPermissions.RolesRead)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("GetPermissionCatalog");

        endpoints.MapGet("/roles", ListRolesAsync)
            .RequirePermission(IdentityPermissions.RolesRead)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("ListRoles");

        endpoints.MapGet("/roles/{code}", GetRoleAsync)
            .RequirePermission(IdentityPermissions.RolesRead)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetRole");

        // Privileged changes answer 401 auth.step_up_required unless the sign-in is recent (US-P1-IAM-05 AC3).
        endpoints.MapPost("/roles", CreateRoleAsync)
            .RequirePermission(IdentityPermissions.RolesManage)
            .Validate<CreateRole>()
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("CreateRole");

        endpoints.MapPut("/roles/{code}", ChangeRoleAsync)
            .RequirePermission(IdentityPermissions.RolesManage)
            .AddEndpointFilter(AddressedRoleExists)
            .Validate<ChangeRole>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("ChangeRole");

        endpoints.MapPut("/roles/{code}/field-levels", ChangeFieldLevelsAsync)
            .RequirePermission(IdentityPermissions.RolesManage)
            .AddEndpointFilter(AddressedRoleExists)
            .Validate<ChangeFieldLevels>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("ChangeRoleFieldLevels");

        endpoints.MapDelete("/roles/{code}", DeleteRoleAsync)
            .RequirePermission(IdentityPermissions.RolesManage)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("DeleteRole");
    }

    private static string? Subject(ClaimsPrincipal user) => user.FindFirst("sub")?.Value;

    /// <summary>Users holding a role on one of these sites (a site-scoped caller's list, US-P1-IAM-06 AC2).</summary>
    private static IQueryable<UserAccount> OnSites(IdentityDbContext db, Guid[] sites, Guid? after) => after is { } cursor
        ? db.Users.FromSql($"""
            SELECT *, xmin FROM identity.users WHERE id > {cursor}
              AND EXISTS (SELECT 1 FROM jsonb_array_elements(roles) AS r WHERE (r->>'siteId')::uuid = ANY({sites}))
            """)
        : db.Users.FromSql($"""
            SELECT *, xmin FROM identity.users
            WHERE EXISTS (SELECT 1 FROM jsonb_array_elements(roles) AS r WHERE (r->>'siteId')::uuid = ANY({sites}))
            """);

    /// <summary>
    /// The addressed role first (before the body is validated): a code this company does not have is "not found"
    /// whatever the request says, so another tenant's role code learns nothing (tenant-isolation suite).
    /// </summary>
    private static async ValueTask<object?> AddressedRoleExists(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var roles = await http.RequestServices.GetRequiredService<RoleDirectory>().LoadAsync(http.RequestAborted).ConfigureAwait(false);
        return http.GetRouteValue("code") is string code && roles.Find(code) is not null
            ? await next(context).ConfigureAwait(false)
            : RoleAdministration.NotFound.ToProblem();
    }

    /// <summary>The addressed user first (before the body is validated): one outside the caller's sites or company is "not found".</summary>
    private static async ValueTask<object?> AddressedUserVisible(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var user = http.GetRouteValue("id") is string text && Guid.TryParse(text, out var id) && id != Guid.Empty
            ? await http.RequestServices.GetRequiredService<IIdentityStore>().FindUserAsync(new UserId(id), http.RequestAborted).ConfigureAwait(false)
            : null;
        return user is not null && http.RequestServices.GetRequiredService<UserAdministration>().CanSee(user)
            ? await next(context).ConfigureAwait(false)
            : UserAdministration.NotFound.ToProblem();
    }

    private static async Task<Results<Ok<UserResponse>, ProblemHttpResult>> ChangeRolesAsync(
        Guid id, ChangeUserRoles request, [FromServices] ChangeUserRolesHandler handler, [FromServices] RoleDirectory roles,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(ToResponse(result.Value, await roles.LoadAsync(cancellationToken).ConfigureAwait(false)))
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<InvitedUser>, ProblemHttpResult>> ResendInvitationAsync(
        Guid id, [FromServices] ResendInvitationHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static UserResponse ToResponse(UserAccount u, TenantRoles roles) =>
        new(u.Id.Value, u.Kind.ToString().ToLowerInvariant(), u.Status.ToString().ToLowerInvariant(), u.FirstName, u.LastName, u.Email, u.Phone, u.EmployeeId,
            [.. u.Roles.Select(r => new RoleAssignmentDto(r.Role, r.SiteId))], u.RequiresMfa(roles.Lookup), u.CreatedAt, u.DeactivatedAt,
            u.PinHash is not null);

    private static RoleResponse ToResponse(RoleView role) =>
        new(role.Code, role.IsDefault, role.Name, role.Description, role.BasedOn, role.Office, role.Privileged,
            [.. role.Permissions.Order(StringComparer.Ordinal)],
            role.FieldLevels.OrderBy(f => f.Key, StringComparer.Ordinal).ToDictionary(f => f.Key, f => f.Value.ToString().ToLowerInvariant(), StringComparer.Ordinal));

    private static async Task<Results<Created<InvitedUser>, ProblemHttpResult>> InviteAsync(
        InviteUser request, [FromServices] InviteUserHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/identity/users/{result.Value.UserId}", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> AcceptAsync(
        AcceptInvitation request, [FromServices] AcceptInvitationHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Results<Created<UserResponse>, ProblemHttpResult>> CreateFieldUserAsync(
        CreateFieldUser request, [FromServices] CreateFieldUserHandler handler, [FromServices] IdentityDbContext db, [FromServices] RoleDirectory roles,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == new UserId(result.Value), cancellationToken).ConfigureAwait(false);
        var tenantRoles = await roles.LoadAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"/api/v1/identity/users/{result.Value}", ToResponse(user, tenantRoles));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> SetPinAsync(
        Guid id, SetPin request, [FromServices] SetPinHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeactivateAsync(
        Guid id, [FromServices] DeactivateUserHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Ok<UserPage>> ListAsync(
        [FromServices] UserAdministration administration, [FromServices] IdentityDbContext db, [FromServices] RoleDirectory roles,
        [FromQuery] Guid? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? 50, 1, MaxPageSize);
        var scope = administration.Access.ScopeOf(IdentityPermissions.UsersRead);
        // Keyset pagination on the UUIDv7 key (creation order). The cursor and site conditions are SQL (EF cannot
        // compare a value-converted ID or look into the roles document; xmin, the concurrency token, is a system column
        // that * does not return); the query filter and row-level security still apply on top of it.
        var users = scope.TenantWide
            ? cursor is { } after ? db.Users.FromSql($"SELECT *, xmin FROM identity.users WHERE id > {after}") : db.Users
            : OnSites(db, [.. scope.Sites], cursor);
        var page = await users.AsNoTracking().OrderBy(u => u.Id).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var tenantRoles = await roles.LoadAsync(cancellationToken).ConfigureAwait(false);
        var items = page.Take(size).Select(u => ToResponse(u, tenantRoles)).ToList();
        return TypedResults.Ok(new UserPage(items, page.Count > size ? items[^1].Id : null));
    }

    private static async Task<Results<Ok<UserResponse>, ProblemHttpResult>> GetAsync(
        Guid id, [FromServices] UserAdministration administration, [FromServices] IIdentityStore store, [FromServices] RoleDirectory roles,
        CancellationToken cancellationToken)
    {
        // Out of the caller's scope answers like a missing user (US-P1-IAM-06 AC2).
        var user = id == Guid.Empty ? null : await store.FindUserAsync(new UserId(id), cancellationToken).ConfigureAwait(false);
        return user is not null && administration.CanSee(user)
            ? TypedResults.Ok(ToResponse(user, await roles.LoadAsync(cancellationToken).ConfigureAwait(false)))
            : UserAdministration.NotFound.ToProblem();
    }

    private static async Task<Results<Ok<UserResponse>, ProblemHttpResult>> MeAsync(
        ClaimsPrincipal caller, [FromServices] IIdentityStore store, [FromServices] RoleDirectory roles, CancellationToken cancellationToken)
    {
        var user = Subject(caller) is { } subject ? await store.FindBySubjectAsync(subject, cancellationToken).ConfigureAwait(false) : null;
        return user is null
            ? SharedKernel.Results.Error.NotFound("identity.user_not_found", "This account has no Karry user in this company.").ToProblem()
            : TypedResults.Ok(ToResponse(user, await roles.LoadAsync(cancellationToken).ConfigureAwait(false)));
    }

    private static Ok<AccessResponse> CurrentAccess([FromServices] ICallerAccess caller) =>
        TypedResults.Ok(new AccessResponse([.. caller.Current.Granted.Order(StringComparer.Ordinal)]));

    private static Ok<PermissionCatalogResponse> PermissionCatalog([FromServices] AuthorizationCatalog catalog) =>
        TypedResults.Ok(new PermissionCatalogResponse(
            [.. catalog.Permissions.OrderBy(p => p.Code, StringComparer.Ordinal)
                .Select(p => new PermissionResponse(p.Code, p.Module, p.Scope.ToString().ToLowerInvariant(), p.Privileged))],
            [.. catalog.SensitiveFields.OrderBy(f => f.Code, StringComparer.Ordinal)
                .Select(f => new SensitiveFieldResponse(f.Code, f.Module, f.Fallback.ToString().ToLowerInvariant()))]));

    private static async Task<Ok<RoleList>> ListRolesAsync([FromServices] RoleDirectory roles, CancellationToken cancellationToken) =>
        TypedResults.Ok(new RoleList([.. (await roles.LoadAsync(cancellationToken).ConfigureAwait(false)).All.Select(ToResponse)]));

    private static async Task<Results<Ok<RoleResponse>, ProblemHttpResult>> GetRoleAsync(
        string code, [FromServices] RoleDirectory roles, CancellationToken cancellationToken) =>
        (await roles.LoadAsync(cancellationToken).ConfigureAwait(false)).Find(code) is { } role
            ? TypedResults.Ok(ToResponse(role))
            : RoleAdministration.NotFound.ToProblem();

    private static async Task<Results<Created<RoleResponse>, ProblemHttpResult>> CreateRoleAsync(
        CreateRole request, [FromServices] RoleAdministration roles, CancellationToken cancellationToken)
    {
        var result = await roles.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/identity/roles/{result.Value.Code}", ToResponse(result.Value))
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<RoleResponse>, ProblemHttpResult>> ChangeRoleAsync(
        string code, ChangeRole request, [FromServices] RoleAdministration roles, CancellationToken cancellationToken)
    {
        var result = await roles.ChangeAsync(code, request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(ToResponse(result.Value)) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<RoleResponse>, ProblemHttpResult>> ChangeFieldLevelsAsync(
        string code, ChangeFieldLevels request, [FromServices] RoleAdministration roles, CancellationToken cancellationToken)
    {
        var result = await roles.ChangeFieldLevelsAsync(code, request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(ToResponse(result.Value)) : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteRoleAsync(
        string code, [FromServices] RoleAdministration roles, CancellationToken cancellationToken)
    {
        var result = await roles.DeleteAsync(code, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }
}

public sealed record RoleAssignmentDto(string Role, Guid? SiteId);

/// <summary>A user; <c>pinSet</c> tells whether a field user has a PIN (an imported one has none until an administrator sets it).</summary>
public sealed record UserResponse(
    Guid Id, string Kind, string Status, string FirstName, string LastName, string? Email, string? Phone, string? EmployeeId,
    IReadOnlyList<RoleAssignmentDto> Roles, bool RequiresMfa, DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt, bool PinSet);

public sealed record UserPage(IReadOnlyList<UserResponse> Items, Guid? NextCursor);

/// <summary>A role: default (name translated by the apps from the code) or custom; field levels of every declared field.</summary>
public sealed record RoleResponse(
    string Code, bool IsDefault, string? Name, string? Description, string? BasedOn, bool Office, bool Privileged,
    IReadOnlyList<string> Permissions, IReadOnlyDictionary<string, string> FieldLevels);

public sealed record RoleList(IReadOnlyList<RoleResponse> Items);

public sealed record PermissionResponse(string Code, string Module, string Scope, bool Privileged);

public sealed record SensitiveFieldResponse(string Code, string Module, string Fallback);

/// <summary>What roles can contain: every permission and sensitive field the modules declare.</summary>
public sealed record PermissionCatalogResponse(IReadOnlyList<PermissionResponse> Permissions, IReadOnlyList<SensitiveFieldResponse> SensitiveFields);

/// <summary>The permissions the caller holds at some scope (display only: every call is still checked).</summary>
public sealed record AccessResponse(IReadOnlyList<string> Permissions);
