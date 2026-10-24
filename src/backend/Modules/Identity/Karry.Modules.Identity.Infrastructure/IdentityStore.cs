using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.Identity.Infrastructure;

internal sealed class IdentityStore(IdentityDbContext db) : IIdentityStore
{
    public Task<UserAccount?> FindUserAsync(UserId id, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<UserAccount?> FindBySubjectAsync(string subject, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(u => u.Subject == subject, cancellationToken);

    public Task<Invitation?> FindInvitationAsync(string tokenHash, CancellationToken cancellationToken) =>
        db.Invitations.SingleOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken);

    public Task<bool> HasAdministratorAsync(CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => u.Status != UserStatus.Deactivated
            && EF.Functions.JsonContains(u.Roles, $"[{{\"role\":\"{Roles.TenantAdmin}\"}}]"), cancellationToken);

    public Task<bool> ExistsAsync(string? email, string? employeeId, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => (email != null && u.Email == email) || (employeeId != null && u.EmployeeId == employeeId), cancellationToken);

    public async Task<Result<UserAccount>> AddAsync(UserAccount user, Invitation? invitation, CancellationToken cancellationToken)
    {
        db.Users.Add(user);
        if (invitation is not null)
        {
            db.Invitations.Add(invitation);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return user;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique)
        {
            db.ChangeTracker.Clear();
            return Error.Conflict("identity.user_exists", unique.ConstraintName switch
            {
                { } name when name.Contains("employee_id", StringComparison.Ordinal) => "Another user already has this employee ID.",
                { } name when name.Contains("phone", StringComparison.Ordinal) => "Another user already has this phone number.",
                _ => "Another user already has this e-mail address.",
            });
        }
    }

    public async Task<Result<bool>> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Error.Conflict("identity.concurrent_change", "Someone else changed this at the same time. Try again.");
        }
    }
}
