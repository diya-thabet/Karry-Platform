using System.Collections.Concurrent;
using Karry.BuildingBlocks.Storage;

namespace Karry.Testing;

/// <summary>Object storage in memory for module tests (the S3 store has its own tests against SeaweedFS).</summary>
public sealed class InMemoryObjectStore : IObjectStore
{
    public ConcurrentDictionary<string, StoredObject> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>Every call fails as a stopped storage does (hardening plan H-R4).</summary>
    public bool Down { get; set; }

    /// <summary>Only removals fail: the storage stops between saving a change and cleaning up after it.</summary>
    public bool DeletesDown { get; set; }

    public bool Available => true;

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        ThrowIfDown(Down);
        Objects[key] = new StoredObject([.. content], contentType);
        return Task.CompletedTask;
    }

    public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ThrowIfDown(Down);
        return Task.FromResult(Objects.TryGetValue(key, out var stored) ? stored : null);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ThrowIfDown(Down || DeletesDown);
        Objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private static void ThrowIfDown(bool down)
    {
        if (down)
        {
            throw new ObjectStoreUnavailableException("Simulated storage outage.");
        }
    }
}
