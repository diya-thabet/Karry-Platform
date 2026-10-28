using Karry.SharedKernel.Domain;

namespace Karry.SharedKernel.UnitTests;

[Trait("Story", "US-P1-AUD-04")]
public sealed class AuditSubjectTests
{
    [EventType("probe.user_renamed.v1")]
    [AuditSubject("user", nameof(UserId))]
    private sealed record UserRenamed(Guid UserId, string Name, string Actor) : DomainEvent;

    [EventType("probe.flag_set.v1")]
    [AuditSubject("flag", nameof(Key))]
    private sealed record FlagSet(string? Key, int Actor) : DomainEvent;

    [EventType("probe.unnamed.v1")]
    private sealed record Unnamed(Guid Id) : DomainEvent;

    [EventType("probe.misnamed.v1")]
    [AuditSubject("thing", "ThingId")]
    private sealed record Misnamed(Guid Id) : DomainEvent;

    private static readonly DateTimeOffset Now = new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_subject_carries_the_entity_its_id_in_canonical_form_and_the_actor()
    {
        var id = Guid.Parse("0197a000-0000-7000-8000-0000000000ff");

        AuditSubject.Of(new UserRenamed(id, "Ali", "user:42") { OccurredAt = Now })
            .Should().Be(new AuditSubject("user", "0197a000-0000-7000-8000-0000000000ff", "user:42"));
    }

    [Fact]
    public void Missing_ids_and_actors_that_are_not_text_stay_empty()
    {
        AuditSubject.Of(new FlagSet(null, 7) { OccurredAt = Now }).Should().Be(new AuditSubject("flag", null, null));
        AuditSubject.Of(new FlagSet("imports.enabled", 7) { OccurredAt = Now }).EntityId.Should().Be("imports.enabled");
    }

    [Fact]
    public void An_event_without_a_subject_or_with_a_wrong_property_cannot_be_ledgered()
    {
        FluentActions.Invoking(() => AuditSubject.Of(new Unnamed(Guid.Empty) { OccurredAt = Now }))
            .Should().Throw<InvalidOperationException>().WithMessage("*[AuditSubject*");
        FluentActions.Invoking(() => AuditSubject.Of(new Misnamed(Guid.Empty) { OccurredAt = Now }))
            .Should().Throw<InvalidOperationException>().WithMessage("*ThingId*");
    }

    [Theory]
    [InlineData("User")]
    [InlineData("shift-pattern")]
    [InlineData("")]
    [InlineData("1user")]
    public void Entity_names_are_lower_snake_case(string entity) =>
        FluentActions.Invoking(() => new AuditSubjectAttribute(entity, "Id")).Should().Throw<ArgumentException>();

    [Fact]
    public void Shift_pattern_is_a_valid_entity_name() =>
        new AuditSubjectAttribute("shift_pattern", "PatternId").Entity.Should().Be("shift_pattern");
}
