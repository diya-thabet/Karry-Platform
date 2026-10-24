using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Identity.Contracts;

/// <summary>Outcome of the first-administrator invitation of a new tenant.</summary>
/// <param name="Invited">False when the tenant already had an administrator (idempotent provisioning).</param>
/// <param name="Delivered">The invitation message was handed to the e-mail service.</param>
public sealed record FirstAdminInvitationResult(bool Invited, bool Delivered);

/// <summary>
/// Invites the first Tenant Admin of a newly provisioned tenant (US-P1-TEN-01 AC1). Called by the Tenancy module when
/// the operator provisions a tenant with an administrator e-mail. Idempotent: once the tenant has an administrator
/// (invited or active) nothing happens.
/// </summary>
public interface IFirstAdminInvitation
{
    Task<Result<FirstAdminInvitationResult>> InviteAsync(TenantId tenant, string firstName, string lastName, string email, string? actor, CancellationToken cancellationToken);
}
