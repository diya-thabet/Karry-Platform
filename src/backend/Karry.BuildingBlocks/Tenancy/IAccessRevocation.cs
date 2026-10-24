using Karry.SharedKernel.Tenancy;

namespace Karry.BuildingBlocks.Tenancy;

/// <summary>
/// Answers whether the account behind a valid token has been revoked since the token was issued (US-P1-IAM-03 AC3,
/// TC-P1-IAM-007): access tokens live 5 minutes, a deactivated user must lose access within one. Implemented by the
/// Identity module (cached, at most 30 s stale); <see cref="TenantResolutionMiddleware"/> asks it on every request
/// that carries a tenant token. A deny-list: subjects Karry does not know are not revoked.
/// </summary>
public interface IAccessRevocation
{
    ValueTask<bool> IsRevokedAsync(TenantId tenant, string subject, CancellationToken cancellationToken);
}
