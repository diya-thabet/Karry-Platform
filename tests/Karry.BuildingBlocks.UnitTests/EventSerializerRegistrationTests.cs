using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.UnitTests;

[EventType("serializerprobe.happened.v1")]
[AuditSubject("probe", nameof(Id))]
public sealed record ProbeHappened(Guid Id) : DomainEvent;

public sealed class SerializerProbeDbContext(DbContextOptions<SerializerProbeDbContext> options) : ModuleDbContext(options)
{
    public override string Schema => "serializerprobe";

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
    }
}

/// <summary>A host with several modules (ADR-0015): each module's dispatcher must read its own events, whatever the registration order.</summary>
public sealed class EventSerializerRegistrationTests
{
    [Fact]
    public void The_serializer_knows_the_events_of_every_module_registered_not_only_the_first()
    {
        var services = new ServiceCollection();
        services.AddOutboxDispatcher<PlatformDbContext>();
        services.AddOutboxDispatcher<SerializerProbeDbContext>();
        services.AddOutboxDispatcher<SerializerProbeDbContext>();

        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<EventSerializer>();

        var id = Guid.NewGuid();
        serializer.Deserialize("serializerprobe.happened.v1", EventSerializer.Serialize(new ProbeHappened(id) { OccurredAt = DateTimeOffset.UnixEpoch }))
            .Should().BeOfType<ProbeHappened>().Which.Id.Should().Be(id, "the second module's events are known, and registering it twice is harmless");
    }
}
