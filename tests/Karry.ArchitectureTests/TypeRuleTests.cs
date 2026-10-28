using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Tenancy;

namespace Karry.ArchitectureTests;

/// <summary>The type rules hold for the real product, and each rule catches the shape it exists for.</summary>
[Trait("Story", "US-P1-PLT-01")]
[Trait("TestCase", "TC-P1-PLT-014")]
public sealed class TypeRuleTests
{
    private static readonly Type[] ProductTypes = [.. TypeRules.ProductAssemblies().SelectMany(a => a.GetTypes())];

    [Fact]
    public void The_product_assemblies_include_the_modules() =>
        TypeRules.ProductAssemblies().Select(a => a.GetName().Name).Should().Contain("Karry.Modules.Tenancy.Domain");

    [Fact]
    public void Product_domain_events_have_stable_unique_type_names() =>
        TypeRules.DomainEventsHaveStableUniqueTypeNames(ProductTypes).Should().BeEmpty();

    [Fact]
    public void Product_domain_events_name_their_audit_subject() =>
        TypeRules.DomainEventsNameTheirAuditSubject(ProductTypes).Should().BeEmpty();

    [Fact]
    public void An_event_without_a_subject_or_naming_a_missing_property_is_a_violation() =>
        TypeRules.DomainEventsNameTheirAuditSubject([typeof(Fine), typeof(WrongSubject), typeof(TypedSubject), typeof(Subjected)])
            .Should().HaveCount(3)
            .And.Contain(v => v.Contains(nameof(TypedSubject), StringComparison.Ordinal))
            .And.Contain(v => v.Contains(nameof(Fine), StringComparison.Ordinal))
            .And.Contain(v => v.Contains(nameof(WrongSubject), StringComparison.Ordinal));

    [Fact]
    public void Product_aggregates_have_no_public_setters() =>
        TypeRules.AggregatesHaveNoPublicSetters(ProductTypes).Should().BeEmpty();

    [Fact]
    public void Product_tenant_ids_are_never_publicly_settable() =>
        TypeRules.TenantIdIsNeverPubliclySettable(ProductTypes).Should().BeEmpty();

    [Fact]
    public void Product_domain_assemblies_reference_no_infrastructure() =>
        TypeRules.DomainAssembliesReferenceNoInfrastructure(TypeRules.ProductAssemblies()).Should().BeEmpty();

    [Fact]
    public void An_event_without_or_with_a_malformed_or_duplicate_name_is_a_violation() =>
        TypeRules.DomainEventsHaveStableUniqueTypeNames([typeof(Unnamed), typeof(Malformed), typeof(First), typeof(Duplicate), typeof(Fine)])
            .Should().HaveCount(3)
            .And.Contain(v => v.Contains(nameof(Unnamed), StringComparison.Ordinal))
            .And.Contain(v => v.Contains(nameof(Malformed), StringComparison.Ordinal))
            .And.Contain(v => v.Contains(nameof(Duplicate), StringComparison.Ordinal));

    [Fact]
    public void An_aggregate_with_a_public_setter_is_a_violation() =>
        TypeRules.AggregatesHaveNoPublicSetters([typeof(LeakyAggregate), typeof(SealedAggregate)])
            .Should().ContainSingle().Which.Should().Contain("LeakyAggregate.Name");

    [Fact]
    public void A_publicly_settable_tenant_id_is_a_violation() =>
        TypeRules.TenantIdIsNeverPubliclySettable([typeof(MovableRow), typeof(FixedRow)])
            .Should().ContainSingle().Which.Should().Contain(nameof(MovableRow));

    public sealed record Unnamed : DomainEvent;

    [EventType("NotAName")]
    public sealed record Malformed : DomainEvent;

    [EventType("probe.thing_happened.v1")]
    public sealed record First : DomainEvent;

    [EventType("probe.thing_happened.v1")]
    public sealed record Duplicate : DomainEvent;

    [EventType("probe.other_thing.v2")]
    public sealed record Fine : DomainEvent;

    [EventType("probe.wrong_subject.v1")]
    [AuditSubject("thing", "ThingId")]
    public sealed record WrongSubject(Guid Id) : DomainEvent;

    [EventType("probe.typed_subject.v1")]
    [AuditSubject("thing", nameof(Thing))]
    public sealed record TypedSubject(ThingId Thing) : DomainEvent;

    [EventType("probe.subjected.v1")]
    [AuditSubject("thing", nameof(Id))]
    public sealed record Subjected(Guid Id) : DomainEvent;

    public readonly record struct ThingId(Guid Value) : IEntityId<ThingId>
    {
        public static ThingId From(Guid value) => new(value);
    }

    public sealed class LeakyAggregate(ThingId id) : AggregateRoot<ThingId>(id)
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class SealedAggregate(ThingId id) : AggregateRoot<ThingId>(id)
    {
        public string Name { get; private set; } = string.Empty;
    }

    public sealed class MovableRow : ITenantOwned
    {
        public TenantId TenantId { get; set; }
    }

    public sealed class FixedRow : ITenantOwned
    {
        public TenantId TenantId { get; init; }
    }
}
