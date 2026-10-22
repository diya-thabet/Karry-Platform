namespace Karry.SharedKernel.Identity;

/// <summary>
/// A strongly typed identifier backed by a UUIDv7 (time-ordered, so B-tree inserts stay local and IDs sort by
/// creation time). Declare one per entity: <c>public readonly record struct AssetId(Guid Value) : IEntityId&lt;AssetId&gt;</c>.
/// </summary>
public interface IEntityId<TSelf>
    where TSelf : struct, IEntityId<TSelf>
{
    Guid Value { get; }

    static abstract TSelf From(Guid value);
}

/// <summary>Creation and validation helpers for <see cref="IEntityId{TSelf}"/>.</summary>
public static class EntityId
{
    /// <summary>New time-ordered ID from the given clock (pass a fake <see cref="TimeProvider"/> in tests).</summary>
    public static TId New<TId>(TimeProvider clock)
        where TId : struct, IEntityId<TId>
    {
        ArgumentNullException.ThrowIfNull(clock);
        return TId.From(Guid.CreateVersion7(clock.GetUtcNow()));
    }

    /// <summary>Wraps an existing value; rejects the empty GUID and non-v7 GUIDs (catches IDs minted elsewhere by mistake).</summary>
    public static TId Parse<TId>(Guid value)
        where TId : struct, IEntityId<TId>
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An entity ID cannot be the empty GUID.", nameof(value));
        }

        if (value.Version != 7)
        {
            throw new ArgumentException($"Entity IDs must be UUIDv7; got version {value.Version}.", nameof(value));
        }

        return TId.From(value);
    }
}
