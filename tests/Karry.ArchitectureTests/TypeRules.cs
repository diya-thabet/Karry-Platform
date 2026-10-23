using System.Reflection;
using System.Text.RegularExpressions;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Tenancy;

namespace Karry.ArchitectureTests;

/// <summary>
/// Type-level rules on the compiled product assemblies (deferred from P1.2 to the first real module, P1.5). Plain
/// reflection: same power as a rule library for these rules, one dependency fewer. Each rule returns violations.
/// </summary>
public static partial class TypeRules
{
    private static readonly string[] InfrastructureAssemblyPrefixes = ["Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql"];

    /// <summary>Every Karry product assembly the host loads (modules, kernel, building blocks).</summary>
    public static IReadOnlyList<Assembly> ProductAssemblies()
    {
        var directory = AppContext.BaseDirectory;
        return Directory.GetFiles(directory, "Karry.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null && !name.EndsWith("Tests", StringComparison.Ordinal) && name != "Karry.Testing")
            .Select(name => Assembly.Load(name!))
            .ToList();
    }

    /// <summary>Events are stored and dispatched by name: a missing, malformed, foreign-prefixed or duplicate name breaks delivery.</summary>
    public static IEnumerable<string> DomainEventsHaveStableUniqueTypeNames(IEnumerable<Type> types)
    {
        var seen = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in types.Where(t => typeof(DomainEvent).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var (name, invalid) = ReadEventType(type);
            if (invalid is not null)
            {
                yield return $"{type.FullName}: invalid [EventType]: {invalid}";
                continue;
            }

            if (name is null)
            {
                yield return $"{type.FullName}: domain event without [EventType(\"module.event.v1\")].";
                continue;
            }

            if (!EventNamePattern().IsMatch(name))
            {
                yield return $"{type.FullName}: event type '{name}' must look like 'module.event_name.v1'.";
            }

            if (ModuleOf(type) is { } module && !name.StartsWith(module + ".", StringComparison.Ordinal))
            {
                yield return $"{type.FullName}: event type '{name}' must start with its module '{module}.'.";
            }

            if (!seen.TryAdd(name, type))
            {
                yield return $"{type.FullName}: event type '{name}' is also used by {seen[name].FullName}.";
            }
        }
    }

    /// <summary>The attribute validates its own format when it is read: report that instead of crashing the rule.</summary>
    private static (string? Name, string? Invalid) ReadEventType(Type type)
    {
        try
        {
            return (type.GetCustomAttribute<EventTypeAttribute>()?.Name, null);
        }
        catch (ArgumentException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Aggregates change only through their methods (invariants, events): no public setter or init.</summary>
    public static IEnumerable<string> AggregatesHaveNoPublicSetters(IEnumerable<Type> types) =>
        from type in types
        where IsAggregate(type) && !type.IsAbstract
        from property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        where property.SetMethod is { IsPublic: true }
        select $"{type.FullName}.{property.Name}: aggregates must not expose a public setter.";

    /// <summary>The tenant of a row is fixed when it is created; nobody can move it to another tenant afterwards.</summary>
    public static IEnumerable<string> TenantIdIsNeverPubliclySettable(IEnumerable<Type> types) =>
        from type in types
        where typeof(ITenantOwned).IsAssignableFrom(type) && type.IsClass
        let property = type.GetProperty(nameof(ITenantOwned.TenantId), BindingFlags.Public | BindingFlags.Instance)
        where property?.SetMethod is { IsPublic: true } setter && !IsInitOnly(setter)
        select $"{type.FullName}.TenantId: must not have a public setter (init or private set only).";

    /// <summary>Domain and kernel assemblies must not even load infrastructure (transitive references included).</summary>
    public static IEnumerable<string> DomainAssembliesReferenceNoInfrastructure(IEnumerable<Assembly> assemblies) =>
        from assembly in assemblies
        let name = assembly.GetName().Name!
        where name.EndsWith(".Domain", StringComparison.Ordinal) || name == "Karry.SharedKernel"
        from reference in assembly.GetReferencedAssemblies()
        where InfrastructurePackagePrefix(reference.Name) is not null
        select $"{name} references {reference.Name}: domain code must stay free of infrastructure.";

    private static string? InfrastructurePackagePrefix(string? name) =>
        InfrastructureAssemblyPrefixes.FirstOrDefault(prefix => name?.StartsWith(prefix, StringComparison.Ordinal) == true);

    private static bool IsAggregate(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(AggregateRoot<>))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    /// <summary><c>Karry.Modules.Tenancy.Domain</c> → <c>tenancy</c>; null outside modules.</summary>
    private static string? ModuleOf(Type type)
    {
        var parts = type.Assembly.GetName().Name!.Split('.');
        return parts is ["Karry", "Modules", var module, ..] ? module.ToLowerInvariant() : null;
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9_]*)+\\.v[1-9][0-9]*$")]
    private static partial Regex EventNamePattern();
}
