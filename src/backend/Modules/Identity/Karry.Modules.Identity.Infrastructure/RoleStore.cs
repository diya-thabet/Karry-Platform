using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.Identity.Infrastructure;

internal sealed class RoleStore(IdentityDbContext db) : IRoleStore
{
    public async Task<IReadOnlyList<CustomRole>> CustomRolesAsync(CancellationToken cancellationToken) =>
        await db.CustomRoles.ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<DefaultRoleSettings>> DefaultRoleSettingsAsync(CancellationToken cancellationToken) =>
        await db.DefaultRoleSettings.ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<bool> IsHeldAsync(string role, CancellationToken cancellationToken) =>
        Holders(role).AnyAsync(cancellationToken);

    public async Task<IReadOnlyList<UserAccount>> AccountHoldersAsync(string role, CancellationToken cancellationToken) =>
        await Holders(role).Where(u => u.Subject != null).ToListAsync(cancellationToken).ConfigureAwait(false);

    private IQueryable<UserAccount> Holders(string role) =>
        db.Users.Where(u => u.Status != UserStatus.Deactivated
            && EF.Functions.JsonContains(u.Roles, System.Text.Json.JsonSerializer.Serialize(new[] { new { role } })));

    public void Add(CustomRole role) => db.CustomRoles.Add(role);

    public void Add(DefaultRoleSettings settings) => db.DefaultRoleSettings.Add(settings);

    public void Remove(CustomRole role) => db.CustomRoles.Remove(role);

    public async Task<Result<bool>> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique)
        {
            db.ChangeTracker.Clear();
            return unique.ConstraintName?.Contains("name", StringComparison.Ordinal) == true
                ? Error.Conflict("identity.role_exists", "Another role already has this name.")
                : Error.Conflict("identity.concurrent_change", "Someone else changed this at the same time. Try again.");
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Error.Conflict("identity.concurrent_change", "Someone else changed this at the same time. Try again.");
        }
    }
}
