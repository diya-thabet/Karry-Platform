using System.Collections.Concurrent;
using Karry.BuildingBlocks.Storage;

namespace Karry.Testing;

/// <summary>Object storage in memory for module tests (the S3 store has its own tests against SeaweedFS).</summary>
public sealed class InMemoryObjectStore : IObjectStore
{
    public ConcurrentDictionary<string, StoredObject> Objects { get; } = new(StringComparer.Ordinal);

    public bool Available => true;

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        Objects[key] = new StoredObject([.. content], contentType);
        return Task.CompletedTask;
    }

    public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Objects.TryGetValue(key, out var stored) ? stored : null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
