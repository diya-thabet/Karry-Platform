using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Karry.SharedKernel.UnitTests;

[Trait("Story", "US-P1-PLT-01")]
[Trait("TestCase", "TC-P1-PLT-018")]
public sealed class AggregateRootTests
{
    private readonly record struct MeterId(Guid Value) : IEntityId<MeterId>
    {
        public static MeterId From(Guid value) => new(value);
    }

    private sealed record ReadingRecorded(TimeProvider Clock, decimal Hours) : DomainEvent(Clock);

    private sealed class Meter(MeterId id, TimeProvider clock) : AggregateRoot<MeterId>(id)
    {
        public void Record(decimal hours) => Raise(new ReadingRecorded(clock, hours));
    }

    [Fact]
    public void Raised_events_are_collected_in_order_with_time_and_id_from_the_clock_until_cleared()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.Zero));
        var meter = new Meter(EntityId.New<MeterId>(clock), clock);

        meter.Record(100m);
        clock.Advance(TimeSpan.FromHours(1));
        meter.Record(101m);

        meter.DomainEvents.Cast<ReadingRecorded>().Select(e => e.Hours).Should().Equal(100m, 101m);
        meter.DomainEvents[0].OccurredAt.Should().Be(new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.Zero));
        meter.DomainEvents.Should().AllSatisfy(e => e.EventId.Version.Should().Be(7));

        meter.ClearDomainEvents();
        meter.DomainEvents.Should().BeEmpty();
    }
}
