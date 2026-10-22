using Karry.SharedKernel.Identity;

namespace Karry.SharedKernel.Domain;

/// <summary>Consistency boundary: the only object other code loads and saves; collects its domain events.</summary>
public abstract class AggregateRoot<TId> : IHasDomainEvents
    where TId : struct, IEntityId<TId>
{
    private readonly List<DomainEvent> domainEvents = [];

    protected AggregateRoot(TId id) => Id = id;

    public TId Id { get; }

    public IReadOnlyList<DomainEvent> DomainEvents => domainEvents;

    /// <summary>Type-qualified ID, so two aggregate types never share an ordering key.</summary>
    public string AggregateKey => $"{GetType().Name}:{Id.Value}";

    /// <summary>Called by the unit of work after the events were written to the outbox.</summary>
    public void ClearDomainEvents() => domainEvents.Clear();

    protected void Raise(DomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        domainEvents.Add(domainEvent);
    }
}
