using Karry.BuildingBlocks.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.UnitTests;

[Trait("Story", "P2.1")]
public sealed class SyncCommandRegistrationTests
{
    [Fact]
    public void Each_type_is_handled_by_the_class_registered_for_it()
    {
        var services = new ServiceCollection();
        services.SyncCommandTypes();
        services.AddSyncCommandHandler<First>("test.first.v1").AddSyncCommandHandler<Second>("test.second.v1");

        var types = services.BuildServiceProvider().GetRequiredService<SyncCommandTypes>();

        types.HandlerOf("test.first.v1").Should().Be<First>();
        types.HandlerOf("test.second.v1").Should().Be<Second>();
        types.HandlerOf("test.third.v1").Should().BeNull();
        services.Count(d => d.ServiceType == typeof(SyncCommandTypes)).Should().Be(1, "one registry, whichever module asks first");
    }

    [Fact]
    public void Two_handlers_for_one_type_stop_the_host_from_starting()
    {
        var services = new ServiceCollection().AddSyncCommandHandler<First>("test.first.v1");

        var act = () => services.AddSyncCommandHandler<Second>("test.first.v1");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Two handlers*test.first.v1*");
    }

    private sealed class First : ISyncCommandHandler
    {
        public string Type => "test.first.v1";

        public Task<HandlerOutcome> HandleAsync(VerifiedCommand command, ISyncTransaction transaction, CancellationToken cancellationToken) =>
            Task.FromResult<HandlerOutcome>(new HandlerOutcome.Accepted(null));
    }

    private sealed class Second : ISyncCommandHandler
    {
        public string Type => "test.second.v1";

        public Task<HandlerOutcome> HandleAsync(VerifiedCommand command, ISyncTransaction transaction, CancellationToken cancellationToken) =>
            Task.FromResult<HandlerOutcome>(new HandlerOutcome.Accepted(null));
    }
}
