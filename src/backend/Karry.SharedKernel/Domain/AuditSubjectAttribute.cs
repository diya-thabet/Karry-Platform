using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Karry.SharedKernel.Domain;

/// <summary>
/// What a ledgered event is about (ADR-0029 §1): the kind of entity (<c>user</c>, <c>asset</c>, <c>role</c>…) and the
/// property holding its ID. The audit viewer filters on it; every event with an <see cref="EventTypeAttribute"/> needs
/// one (architecture test). The actor is the event's <c>Actor</c> property when it has one.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed partial class AuditSubjectAttribute : Attribute
{
    public AuditSubjectAttribute(string entity, string idProperty)
    {
        if (!EntityPattern().IsMatch(entity ?? string.Empty))
        {
            throw new ArgumentException($"Audit entity '{entity}' must look like 'user' or 'shift_pattern'.", nameof(entity));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(idProperty);
        Entity = entity!;
        IdProperty = idProperty;
    }

    public string Entity { get; }

    public string IdProperty { get; }

    [GeneratedRegex("^[a-z][a-z_]*$")]
    private static partial Regex EntityPattern();
}

/// <summary>Who did what to which entity, read from an event through its <see cref="AuditSubjectAttribute"/>.</summary>
public sealed record AuditSubject(string Entity, string? EntityId, string? Actor)
{
    /// <summary>The property naming who acted, on events that have one (the caller's subject, or a system name).</summary>
    public const string ActorProperty = "Actor";

    private static readonly ConcurrentDictionary<Type, Func<object, AuditSubject>> Readers = new();

    /// <summary>The subject of an event; throws when its type declares none or names a property it does not have.</summary>
    public static AuditSubject Of(object domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return Readers.GetOrAdd(domainEvent.GetType(), Reader)(domainEvent);
    }

    private static Func<object, AuditSubject> Reader(Type type)
    {
        var subject = type.GetCustomAttribute<AuditSubjectAttribute>()
            ?? throw new InvalidOperationException($"{type.FullName} has no [AuditSubject(\"entity\", nameof(Id))] attribute; every ledgered event names its subject.");
        var id = type.GetProperty(subject.IdProperty)
            ?? throw new InvalidOperationException($"{type.FullName} has no property {subject.IdProperty} named by its [AuditSubject].");
        var actor = type.GetProperty(ActorProperty) is { PropertyType: var actorType } property && actorType == typeof(string) ? property : null;
        return e => new AuditSubject(subject.Entity, Text(id.GetValue(e)), actor?.GetValue(e) as string);
    }

    private static string? Text(object? value) => value switch
    {
        null => null,
        Guid guid => guid.ToString("D"),
        string text => text,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };
}
