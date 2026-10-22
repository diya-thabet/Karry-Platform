using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using Karry.SharedKernel.Domain;

namespace Karry.BuildingBlocks.Outbox;

/// <summary>JSON (de)serialization of domain events by their stable <see cref="EventTypeAttribute"/> name.</summary>
public sealed class EventSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly FrozenDictionary<string, Type> typesByName;

    /// <summary>Knows every <see cref="DomainEvent"/> type with an <see cref="EventTypeAttribute"/> in the given assemblies.</summary>
    public EventSerializer(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var types = assemblies.Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(DomainEvent).IsAssignableFrom(t) && !t.IsAbstract && t.GetCustomAttribute<EventTypeAttribute>() is not null)
            .ToList();

        var duplicate = types.GroupBy(EventTypeAttribute.NameOf).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Event type name '{duplicate.Key}' is used by {string.Join(" and ", duplicate.Select(t => t.FullName))}.");
        }

        typesByName = types.ToFrozenDictionary(EventTypeAttribute.NameOf, StringComparer.Ordinal);
    }

    public static string Serialize(DomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), Options);
    }

    /// <summary>Null when the type name is unknown (e.g. produced by a newer version): the dispatcher dead-letters it.</summary>
    public DomainEvent? Deserialize(string typeName, string payload) =>
        typesByName.TryGetValue(typeName, out var type) ? (DomainEvent?)JsonSerializer.Deserialize(payload, type, Options) : null;
}
