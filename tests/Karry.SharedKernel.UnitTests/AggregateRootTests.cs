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

    [EventType("assets.reading_recorded.v1")]
    [AuditSubject("reading", nameof(Hours))]
    private sealed record ReadingRecorded(decimal Hours) : DomainEvent;

    private sealed record Unnamed : DomainEvent;

    private sealed class Meter(MeterId id, TimeProvider clock) : AggregateRoot<MeterId>(id)
    {
        public void Record(decimal hours) => Raise(new ReadingRecorded(hours) { OccurredAt = clock.GetUtcNow() });
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


        meter.DomainEvents.Select(e => e.EventId).Should().OnlyHaveUniqueItems();
        meter.AggregateKey.Should().Be($"Meter:{meter.Id.Value}");

        meter.ClearDomainEvents();
        meter.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void Events_have_a_stable_versioned_name_and_unnamed_events_are_refused()
    {
        EventTypeAttribute.NameOf(typeof(ReadingRecorded)).Should().Be("assets.reading_recorded.v1");
        ((Action)(() => EventTypeAttribute.NameOf(typeof(Unnamed)))).Should().Throw<InvalidOperationException>().WithMessage("*EventType*");
    }

    [Theory]
    [InlineData("Assets.Reading.v1")]
    [InlineData("assets.reading")]
    [InlineData("assets.reading.v0")]
    [InlineData("reading.v1")]
    public void Badly_formed_event_names_are_rejected(string name)
    {
        ((Action)(() => _ = new EventTypeAttribute(name))).Should().Throw<ArgumentException>();
    }
}
