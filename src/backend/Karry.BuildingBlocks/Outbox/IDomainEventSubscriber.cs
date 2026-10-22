using Karry.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.BuildingBlocks.Outbox;

/// <summary>
/// Subscriber that reacts to a dispatched event (named "subscriber": the .NET convention reserves "EventHandler" for delegates). Delivery is at least once. Database work done through
/// <see cref="DomainEventContext.Db"/> runs in the dispatcher's transaction, so it is applied exactly once
/// together with the "processed" marker. External side effects (e-mail, HTTP) must be idempotent.
/// </summary>
public interface IDomainEventSubscriber<in TEvent>
    where TEvent : DomainEvent
{
    Task HandleAsync(TEvent domainEvent, DomainEventContext context, CancellationToken cancellationToken);
}

/// <summary>Dispatch context: the outbox message ID and the module DbContext enlisted in the dispatch transaction.</summary>
public sealed record DomainEventContext(Guid MessageId, DbContext Db);
