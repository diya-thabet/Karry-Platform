using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Karry.BuildingBlocks.Api;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Karry.BuildingBlocks.Authorization;

/// <summary>
/// Field-level masking in the API's JSON contract (US-P1-IAM-07, ADR-0022), the one place every response passes:
/// a property marked <see cref="SensitiveAttribute"/> is written only when the caller's level for it, on the object's
/// site (<see cref="ISiteScoped"/>), is Read or Write. Masked: not written, its JSON name listed in
/// <see cref="MaskedFieldsProperty"/>. Hidden: not written, not listed. The value itself is never written in any form.
/// </summary>
public static class FieldMasking
{
    /// <summary>JSON property listing the masked fields of an object (the UI shows <c>•••</c> "hidden by policy").</summary>
    public const string MaskedFieldsProperty = "maskedFields";

    /// <summary>OpenAPI extension naming the sensitive field of a property.</summary>
    public const string OpenApiExtension = "x-karry-sensitive";

    public const string NotWritableCode = "auth.field_not_writable";

    /// <summary>The contract modifier; <paramref name="access"/> gives the current caller's access (Nobody outside a request).</summary>
    public static Action<JsonTypeInfo> Modifier(Func<CallerAccess> access)
    {
        ArgumentNullException.ThrowIfNull(access);
        return info =>
        {
            if (info.Kind != JsonTypeInfoKind.Object)
            {
                return;
            }

            var sensitive = info.Properties.Select(p => (Property: p, Field: FieldOf(p))).Where(p => p.Field is not null).ToList();
            if (sensitive.Count == 0)
            {
                return;
            }

            if (info.Properties.Any(p => p.Name == MaskedFieldsProperty))
            {
                throw new InvalidOperationException($"{info.Type} has sensitive fields and a property named '{MaskedFieldsProperty}'.");
            }

            foreach (var (property, field) in sensitive)
            {
                var original = property.ShouldSerialize;
                property.ShouldSerialize = (owner, value) => Level(access(), field!, owner) >= FieldAccess.Read && (original?.Invoke(owner, value) ?? true);
                property.IsRequired = false; // absent when masked or hidden
            }

            var masked = info.CreateJsonPropertyInfo(typeof(IReadOnlyList<string>), MaskedFieldsProperty);
            masked.Get = owner =>
            {
                var caller = access();
                return sensitive.Where(s => Level(caller, s.Field!, owner) == FieldAccess.Masked).Select(s => s.Property.Name).ToList();
            };
            masked.ShouldSerialize = (_, value) => value is IReadOnlyList<string> { Count: > 0 };
            info.Properties.Add(masked);
        };
    }

    /// <summary>The caller's level for <paramref name="field"/> on <paramref name="owner"/> (its site when it has one).</summary>
    public static FieldAccess Level(CallerAccess caller, string field, object? owner)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller.FieldAccessFor(field, (owner as ISiteScoped)?.SiteId);
    }

    /// <summary>The sensitive field of a JSON property: on the property, or on the record's constructor parameter.</summary>
    public static string? FieldOf(JsonPropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return Attribute(property.AttributeProvider) ?? Attribute(property.AssociatedParameter?.AttributeProvider);
    }

    private static string? Attribute(ICustomAttributeProvider? provider) =>
        (provider?.GetCustomAttributes(typeof(SensitiveAttribute), inherit: true).FirstOrDefault() as SensitiveAttribute)?.Field;

    /// <summary>Masks every HTTP JSON response of the host and refuses sensitive input the caller may not write.</summary>
    public static IServiceCollection AddKarryFieldMasking(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<IConfigureOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>, ConfigureJson>();
        return services;
    }

    /// <summary>Marks sensitive properties in the OpenAPI document (<c>x-karry-sensitive</c>) for contract reviews.</summary>
    public static OpenApiOptions AddSensitiveFieldMarks(this OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.AddSchemaTransformer((schema, context, _) =>
        {
            if (context.JsonPropertyInfo is null && schema.Required is { Count: > 0 } required)
            {
                // A masked or hidden field is absent from the object, so it is never required.
                foreach (var name in context.JsonTypeInfo.Properties.Where(p => FieldOf(p) is not null).Select(p => p.Name))
                {
                    required.Remove(name);
                }
            }

            if (context.JsonPropertyInfo is { } property && FieldOf(property) is { } field && schema is OpenApiSchema target)
            {
                target.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                target.Extensions[OpenApiExtension] = new JsonNodeExtension(JsonValue.Create(field));
                target.Description = $"Sensitive field `{field}`: absent when the caller's role masks or hides it (then listed in `{MaskedFieldsProperty}` when masked).";
            }

            return Task.CompletedTask;
        });
    }

    private sealed class ConfigureJson(IHttpContextAccessor http) : IConfigureOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>
    {
        public void Configure(Microsoft.AspNetCore.Http.Json.JsonOptions options)
        {
            var resolver = options.SerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
            options.SerializerOptions.TypeInfoResolver = resolver.WithAddedModifier(Modifier(() =>
                http.HttpContext?.RequestServices.GetService<ICallerAccess>()?.Current ?? CallerAccess.Nobody));
        }
    }
}

/// <summary>
/// Refuses a request that sets a sensitive field the caller may not write (US-P1-IAM-07 AC1 "WRITE"): 403
/// <c>auth.field_not_writable</c>. A value counts as set when it is not null, so writable sensitive inputs are nullable.
/// Only parameters whose type contains sensitive fields (directly, in nested objects or in lists of them) are looked
/// at, decided once per endpoint from the types: services and other arguments are never touched.
/// </summary>
public static class SensitiveInputFilter
{
    private const int MaxDepth = 8;
    private static readonly ConcurrentDictionary<Type, Member[]> Members = new();

    private sealed record Member(PropertyInfo Property, string? Field, bool Collection);

    public static EndpointFilterDelegate Create(EndpointFilterFactoryContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var inputs = context.MethodInfo.GetParameters()
            .Where(p => p.GetCustomAttribute<Microsoft.AspNetCore.Mvc.FromServicesAttribute>() is null && MembersOf(p.ParameterType).Length > 0)
            .Select(p => p.Position).ToArray();
        if (inputs.Length == 0)
        {
            return next;
        }

        return async invocation =>
        {
            var caller = invocation.HttpContext.RequestServices.GetService<ICallerAccess>()?.Current ?? CallerAccess.Nobody;
            foreach (var index in inputs)
            {
                if (FirstRefused(invocation.Arguments[index], caller, site: null, depth: 0) is { } field)
                {
                    var problem = Error.Forbidden(FieldMasking.NotWritableCode, "You are not allowed to set this field.").ToProblem();
                    problem.ProblemDetails.Extensions["field"] = field;
                    return problem;
                }
            }

            return await next(invocation).ConfigureAwait(false);
        };
    }

    private static string? FirstRefused(object? value, CallerAccess caller, Guid? site, int depth)
    {
        if (value is null || depth > MaxDepth)
        {
            return null;
        }

        var objectSite = value is ISiteScoped scoped ? scoped.SiteId : site;
        foreach (var member in MembersOf(value.GetType()))
        {
            var content = member.Property.GetValue(value);
            if (member.Field is { } field)
            {
                if (content is not null && caller.FieldAccessFor(field, objectSite) < FieldAccess.Write)
                {
                    return field;
                }
            }
            else if (member.Collection)
            {
                foreach (var item in (IEnumerable?)content ?? Array.Empty<object>())
                {
                    if (FirstRefused(item, caller, objectSite, depth + 1) is { } nested)
                    {
                        return nested;
                    }
                }
            }
            else if (FirstRefused(content, caller, objectSite, depth + 1) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>The members leading to sensitive fields: the fields themselves, and objects or lists that contain some.</summary>
    private static Member[] MembersOf(Type type)
    {
        if (type.IsPrimitive || type == typeof(string) || type.Assembly.GetName().Name?.StartsWith("Karry.", StringComparison.Ordinal) != true)
        {
            return [];
        }

        return Members.TryGetValue(type, out var known) ? known : Describe(type);
    }

    private static Member[] Describe(Type type)
    {
        Members[type] = []; // while describing: a type that refers to itself does not recurse forever
        var parameters = type.GetConstructors().SelectMany(c => c.GetParameters())
            .Select(p => (p.Name, p.GetCustomAttribute<SensitiveAttribute>()?.Field)).Where(p => p.Field is not null)
            .GroupBy(p => p.Name!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Field!, StringComparer.Ordinal);
        var members = new List<Member>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
        {
            if ((property.GetCustomAttribute<SensitiveAttribute>()?.Field ?? parameters.GetValueOrDefault(property.Name)) is { } field)
            {
                members.Add(new Member(property, field, Collection: false));
            }
            else if (ElementType(property.PropertyType) is { } element && MembersOf(element).Length > 0)
            {
                members.Add(new Member(property, null, Collection: true));
            }
            else if (MembersOf(property.PropertyType).Length > 0)
            {
                members.Add(new Member(property, null, Collection: false));
            }
        }

        Member[] described = [.. members];
        Members[type] = described;
        return described;
    }

    private static Type? ElementType(Type type) =>
        type == typeof(string) ? null : type.IsArray ? type.GetElementType()
            : type.GetInterfaces().Append(type).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
}

/// <summary>A column of an export (CSV, Excel, PDF), optionally a sensitive field.</summary>
public sealed record ExportColumn<T>(string Header, Func<T, object?> Value, string? SensitiveField = null);

/// <summary>
/// The same masking for exports and PDFs (US-P1-IAM-07 AC2): a sensitive column hidden from the caller on every row is
/// left out; on a row where the caller may not read it, the cell is <see cref="MaskedText"/>. Never the value.
/// </summary>
public static class SensitiveExport
{
    public const string MaskedText = "•••";

    public static IReadOnlyList<ExportColumn<T>> Columns<T>(CallerAccess caller, IEnumerable<ExportColumn<T>> columns, IReadOnlyCollection<T> rows)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        return
        [
            .. columns.Where(c => c.SensitiveField is not { } field
                || (rows.Count == 0 ? caller.FieldAccessFor(field, null) : rows.Max(r => FieldMasking.Level(caller, field, r))) > FieldAccess.Hidden),
        ];
    }

    public static object? Cell<T>(CallerAccess caller, ExportColumn<T> column, T row)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(column);
        return column.SensitiveField is { } field && FieldMasking.Level(caller, field, row) < FieldAccess.Read ? MaskedText : column.Value(row);
    }
}
