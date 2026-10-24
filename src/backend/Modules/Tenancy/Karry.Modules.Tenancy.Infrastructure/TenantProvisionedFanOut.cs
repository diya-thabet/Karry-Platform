using Karry.BuildingBlocks.Outbox;
using Karry.Modules.Tenancy.Contracts;
using Karry.Modules.Tenancy.Domain;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Tenancy.Infrastructure;

/// <summary>
/// Publishes <see cref="TenantProvisioned"/> to other modules through <see cref="ITenantProvisionedHandler"/> (they may
/// not reference this module's domain events). A failing handler fails the message, which the dispatcher retries for
/// every handler: they are idempotent by contract.
/// </summary>
internal sealed class TenantProvisionedFanOut(IEnumerable<ITenantProvisionedHandler> handlers) : IDomainEventSubscriber<TenantProvisioned>
{
    public async Task HandleAsync(TenantProvisioned domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var tenant = new TenantSummary(new TenantId(domainEvent.TenantId), domainEvent.Slug, domainEvent.Name);
        foreach (var handler in handlers)
        {
            await handler.HandleAsync(tenant, cancellationToken).ConfigureAwait(false);
        }
    }
}
