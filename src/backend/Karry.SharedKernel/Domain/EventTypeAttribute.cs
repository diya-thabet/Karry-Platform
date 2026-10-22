using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Karry.SharedKernel.Domain;

/// <summary>
/// Stable name of a domain event in stored data (outbox, integration events): <c>{module}.{event}.v{n}</c>.
/// CLR type names are never persisted, so classes can be renamed or moved without breaking stored events.
/// A breaking change to an event's shape is a new version (<c>.v2</c>), not an edit.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed partial class EventTypeAttribute : Attribute
{
    private static readonly ConcurrentDictionary<Type, string> Cache = new();

    public EventTypeAttribute(string name)
    {
        if (!NamePattern().IsMatch(name ?? string.Empty))
        {
            throw new ArgumentException($"Event type name '{name}' must look like 'module.event_name.v1'.", nameof(name));
        }

        Name = name!;
    }

    public string Name { get; }

    /// <summary>The stable name of an event type; throws if the type has no <see cref="EventTypeAttribute"/>.</summary>
    public static string NameOf(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return Cache.GetOrAdd(eventType, type =>
            type.GetCustomAttribute<EventTypeAttribute>()?.Name
            ?? throw new InvalidOperationException($"{type.FullName} has no [EventType(\"module.event.v1\")] attribute; every domain event needs a stable name."));
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]*\.[a-z][a-z0-9_]*\.v[1-9][0-9]*$")]
    private static partial Regex NamePattern();
}
