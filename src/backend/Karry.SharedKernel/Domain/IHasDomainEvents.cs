namespace Karry.SharedKernel.Domain;

/// <summary>Non-generic view of an aggregate's pending events, used by the unit of work to fill the outbox.</summary>
public interface IHasDomainEvents
{
    /// <summary>Key that orders events: all events of one aggregate are dispatched in the order they were raised.</summary>
    string AggregateKey { get; }

    IReadOnlyList<DomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
