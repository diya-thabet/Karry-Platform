using System.Security.Claims;
using FluentValidation;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
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

/// <summary>Users, invitations and deactivation (US-P1-IAM-03). Routes under <c>/api/v1/identity</c>.</summary>
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
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
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
            .AllowAnyTenantMember()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetCurrentUser");
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

    private static UserResponse ToResponse(UserAccount u) =>
        new(u.Id.Value, u.Kind.ToString().ToLowerInvariant(), u.Status.ToString().ToLowerInvariant(), u.FirstName, u.LastName, u.Email, u.Phone, u.EmployeeId,
            [.. u.Roles.Select(r => new RoleAssignmentDto(r.Role, r.SiteId))], u.RequiresMfa, u.CreatedAt, u.DeactivatedAt);

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
        CreateFieldUser request, [FromServices] CreateFieldUserHandler handler, [FromServices] IdentityDbContext db, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == new UserId(result.Value), cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"/api/v1/identity/users/{result.Value}", ToResponse(user));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeactivateAsync(
        Guid id, [FromServices] DeactivateUserHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Ok<UserPage>> ListAsync(
        [FromServices] UserAdministration administration, [FromServices] IdentityDbContext db,
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
        var items = page.Take(size).Select(ToResponse).ToList();
        return TypedResults.Ok(new UserPage(items, page.Count > size ? items[^1].Id : null));
    }

    private static async Task<Results<Ok<UserResponse>, ProblemHttpResult>> GetAsync(
        Guid id, [FromServices] UserAdministration administration, [FromServices] IIdentityStore store, CancellationToken cancellationToken)
    {
        // Out of the caller's scope answers like a missing user (US-P1-IAM-06 AC2).
        var user = id == Guid.Empty ? null : await store.FindUserAsync(new UserId(id), cancellationToken).ConfigureAwait(false);
        return user is not null && administration.CanSee(user) ? TypedResults.Ok(ToResponse(user)) : UserAdministration.NotFound.ToProblem();
    }

    private static async Task<Results<Ok<UserResponse>, ProblemHttpResult>> MeAsync(
        ClaimsPrincipal caller, [FromServices] IIdentityStore store, CancellationToken cancellationToken)
    {
        var user = Subject(caller) is { } subject ? await store.FindBySubjectAsync(subject, cancellationToken).ConfigureAwait(false) : null;
        return user is null
            ? SharedKernel.Results.Error.NotFound("identity.user_not_found", "This account has no Karry user in this company.").ToProblem()
            : TypedResults.Ok(ToResponse(user));
    }
}

public sealed record RoleAssignmentDto(string Role, Guid? SiteId);

public sealed record UserResponse(
    Guid Id, string Kind, string Status, string FirstName, string LastName, string? Email, string? Phone, string? EmployeeId,
    IReadOnlyList<RoleAssignmentDto> Roles, bool RequiresMfa, DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt);

public sealed record UserPage(IReadOnlyList<UserResponse> Items, Guid? NextCursor);
