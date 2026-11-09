using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.Sync;

/// <summary>
/// A command a tablet signed, after the Sync module checked its signature, its place in the device's sequence and
/// its type (ADR-0031 §7). <see cref="SignedText"/> is the exact text the device key signed.
/// </summary>
public sealed record VerifiedCommand(
    Guid CommandId,
    Guid DeviceId,
    Guid SiteId,
    long Sequence,
    string Type,
    JsonElement Payload,
    DateTimeOffset DeviceTime,
    DateTimeOffset ReceivedAt,
    int ClockOffsetMs,
    string SignedText);

/// <summary>
/// What a handler made of a command (ADR-0031 §3). A field fact is never refused for a business reason: it is stored,
/// and flagged when it needs a look. <see cref="Rejected"/> is for structural problems only.
/// </summary>
public abstract record HandlerOutcome
{
    private HandlerOutcome()
    {
    }

    /// <summary>The fact is stored.</summary>
    public sealed record Accepted(Guid? RecordId) : HandlerOutcome;

    /// <summary>The fact is stored and flagged for review. Final: the tablet never sends it again.</summary>
    public sealed record Conflict(Guid? RecordId, IReadOnlyList<string> Flags) : HandlerOutcome;

    /// <summary>Not stored (payload not of the type's shape, a missing blob…). Retryable: the tablet keeps it and sends it again.</summary>
    public sealed record Rejected(string Code, bool Retryable) : HandlerOutcome;
}

/// <summary>
/// The upload's database transaction, which a handler joins (ADR-0031 §2): its rows, ledger entries and outbox events
/// commit with the Sync module's bookkeeping, or not at all. A handler never commits, never opens a transaction of its
/// own and never calls a remote service.
/// </summary>
public interface ISyncTransaction
{
    DbConnection Connection { get; }

    DbTransaction Transaction { get; }
}

/// <summary>Handles one versioned command type, e.g. <c>fieldops.shift_entry.start.v1</c> (ADR-0031 §5).</summary>
public interface ISyncCommandHandler
{
    /// <summary>The command type this handler owns; one handler per type.</summary>
    string Type { get; }

    Task<HandlerOutcome> HandleAsync(VerifiedCommand command, ISyncTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>The command types the host can handle, collected when handlers are registered.</summary>
public sealed class SyncCommandTypes
{
    private readonly Dictionary<string, Type> handlers = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Types => handlers.Keys;

    /// <summary>The handler class of <paramref name="type"/>, or null when no module handles it.</summary>
    public Type? HandlerOf(string type) => handlers.GetValueOrDefault(type);

    internal void Add(string type, Type handler)
    {
        if (!handlers.TryAdd(type, handler))
        {
            throw new InvalidOperationException(
                $"Two handlers for the sync command type '{type}': {handlers[type].FullName} and {handler.FullName}.");
        }
    }
}

public static class SyncCommandRegistration
{
    /// <summary>
    /// Registers <typeparamref name="THandler"/> for <paramref name="type"/>. A second handler for the same type stops
    /// the host while it starts (ADR-0031 §5).
    /// </summary>
    public static IServiceCollection AddSyncCommandHandler<THandler>(this IServiceCollection services, string type)
        where THandler : class, ISyncCommandHandler
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        services.SyncCommandTypes().Add(type, typeof(THandler));
        services.AddScoped<THandler>();
        return services;
    }

    /// <summary>The registry of handled types, registered once whichever module asks first.</summary>
    public static SyncCommandTypes SyncCommandTypes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.FirstOrDefault(d => d.ServiceType == typeof(SyncCommandTypes))?.ImplementationInstance is SyncCommandTypes types)
        {
            return types;
        }

        types = new SyncCommandTypes();
        services.AddSingleton(types);
        return types;
    }
}
