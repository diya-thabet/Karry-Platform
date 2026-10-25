using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Karry.BuildingBlocks.Authorization;
using Karry.SharedKernel.Authorization;
using Karry.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Karry.AuthorizationTests;

/// <summary>
/// Masking suite (US-P1-IAM-07 AC3, TC-P1-IAM-005, threat T-10), generated from the committed API contract and from
/// the types of the real host, so a new module is covered without editing this file:
/// <list type="number">
/// <item>every property of the contract that looks sensitive (price, cost, wage, margin, loan …) is marked as a
/// sensitive field, and every mark names a field a module declares;</item>
/// <item>every success response is JSON (so it goes through the one masking serializer), or is a reviewed export;</item>
/// <item>every API type with sensitive fields, serialized by the host's own JSON options with canary values, never
/// shows a masked or hidden value, lists masked ones, and shows readable ones (positive control).</item>
/// </list>
/// </summary>
[Trait("Story", "US-P1-IAM-07")]
[Trait("TestCase", "TC-P1-IAM-005")]
public sealed partial class MaskingSuite : IDisposable
{
    /// <summary>Words that make a property name look like money or pay. Matched on whole camelCase words.</summary>
    private static readonly HashSet<string> SensitiveWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "price", "prices", "cost", "costs", "wage", "wages", "salary", "salaries", "payroll", "margin", "margins",
        "loan", "loans", "revenue", "revenues", "profit", "profits", "amount", "amounts", "rate", "rates", "discount",
        "tariff", "tariffs",
    };

    /// <summary>Contract properties that look sensitive but are not (<c>Schema.property</c> → reason). Adding one is a review.</summary>
    private static readonly Dictionary<string, string> NotSensitive = new(StringComparer.Ordinal)
    {
        ["PlanResponse.prices"] = "Karry's own list price book of a plan (platform data, platform operators only), not a customer's commercial data.",
        ["PublishPlan.prices"] = "The same price book, sent by a platform operator when publishing a plan version.",
        ["InvoiceResponse.amount"] = "Karry's invoice to the tenant itself, not the tenant's business data; behind billing.subscription.read (admin, executive, finance) or operators.",
        ["RecordInvoice.amount"] = "The same invoice amount, recorded by a platform operator.",
    };

    /// <summary>Success responses that are not JSON (files): each must be written with <see cref="SensitiveExport"/>.</summary>
    private static readonly Dictionary<string, string> ReviewedExports = new(StringComparer.Ordinal);

    private static readonly JsonElement Contract = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(PostgresServer.RepositoryRoot(), "src", "backend", "Karry.Host", "openapi", "v1.json"))).RootElement;

    private readonly HostFactory factory = new();

    public void Dispose() => factory.Dispose();

    [Fact]
    public void Every_property_of_the_contract_that_looks_sensitive_is_a_marked_sensitive_field()
    {
        var unmarked = Properties()
            .Where(p => Words(p.Property).Any(SensitiveWords.Contains) && !p.Schema.TryGetProperty(FieldMasking.OpenApiExtension, out _))
            .Select(p => $"{p.Owner}.{p.Property}")
            .Where(name => !NotSensitive.ContainsKey(name))
            .ToList();

        unmarked.Should().BeEmpty("mark these properties [Sensitive(\"module.resource.field\")] and declare the field, or list them in NotSensitive with a reason");
    }

    [Fact]
    public void Every_marked_property_names_a_sensitive_field_a_module_declares()
    {
        var catalog = factory.Services.GetRequiredService<AuthorizationCatalog>();

        var unknown = Properties()
            .Where(p => p.Schema.TryGetProperty(FieldMasking.OpenApiExtension, out var code) && catalog.SensitiveField(code.GetString()!) is null)
            .Select(p => $"{p.Owner}.{p.Property}")
            .ToList();

        unknown.Should().BeEmpty("an undeclared field is hidden from everyone; declare it in the module's SensitiveFields");
    }

    [Fact]
    public void Every_success_response_is_json_or_a_reviewed_export()
    {
        var others = new List<string>();
        foreach (var path in Contract.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var id = operation.Value.GetProperty("operationId").GetString()!;
                foreach (var response in operation.Value.GetProperty("responses").EnumerateObject().Where(r => r.Name.StartsWith('2')))
                {
                    var types = response.Value.TryGetProperty("content", out var content) ? content.EnumerateObject().Select(c => c.Name).ToList() : [];
                    if (types.Any(t => t != "application/json") && !ReviewedExports.ContainsKey(id))
                    {
                        others.Add($"{id} {response.Name}: {string.Join(", ", types)}");
                    }
                }
            }
        }

        others.Should().BeEmpty("non-JSON responses bypass the masking serializer; write them with SensitiveExport and review them here");
    }

    public static TheoryData<string> SensitiveTypes()
    {
        var data = new TheoryData<string>();
        foreach (var type in TypesWithSensitiveFields())
        {
            data.Add(type.AssemblyQualifiedName!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SensitiveTypes))]
    public void A_masked_or_hidden_field_never_appears_in_the_hosts_json(string typeName)
    {
        var type = Type.GetType(typeName, throwOnError: true)!;
        var options = factory.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var info = options.GetTypeInfo(type);
        var fields = info.Properties.Select(p => (p.Name, Field: FieldMasking.FieldOf(p))).Where(p => p.Field is not null).ToList();
        var (instance, canaries) = WithCanaries(type, fields.Select(f => f.Name).ToList(), info);

        foreach (var (name, field) in fields)
        {
            foreach (var level in new[] { FieldAccess.Masked, FieldAccess.Hidden, FieldAccess.Read })
            {
                var json = SerializeAs(instance, type, options, fields.ToDictionary(f => f.Field!, f => f.Field == field ? level : FieldAccess.Read, StringComparer.Ordinal));
                var listed = JsonDocument.Parse(json).RootElement.TryGetProperty(FieldMasking.MaskedFieldsProperty, out var masked)
                    && masked.EnumerateArray().Any(m => m.GetString() == name);

                if (level == FieldAccess.Read)
                {
                    json.Should().Contain(canaries[name], $"positive control: {type.Name}.{name} is readable, so its canary must show");
                    listed.Should().BeFalse();
                }
                else
                {
                    json.Should().NotContain(canaries[name], $"{type.Name}.{name} is {level}");
                    listed.Should().Be(level == FieldAccess.Masked, $"{type.Name}.{name} is {level}");
                }
            }
        }
    }

    [Fact]
    public void The_generator_finds_types_with_sensitive_fields_including_this_suites_probe()
    {
        TypesWithSensitiveFields().Should().Contain(typeof(ProbeQuote), "otherwise the theory above would silently test nothing");
    }

    private static IEnumerable<Type> TypesWithSensitiveFields()
    {
        // Module assemblies of the host (loaded through Karry.Host's references) and this suite's probe.
        var assemblies = typeof(Program).Assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("Karry.", StringComparison.Ordinal))
            .Select(Assembly.Load).Append(typeof(MaskingSuite).Assembly).Distinct();
        return assemblies.SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } || t is { IsValueType: true, IsEnum: false })
            .Where(t => t.GetProperties().Any(p => p.GetCustomAttribute<SensitiveAttribute>() is not null)
                || t.GetConstructors().SelectMany(c => c.GetParameters()).Any(p => p.GetCustomAttribute<SensitiveAttribute>() is not null))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
    }

    private string SerializeAs(object instance, Type type, JsonSerializerOptions options, Dictionary<string, FieldAccess> levels)
    {
        var catalog = factory.Services.GetRequiredService<AuthorizationCatalog>();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICallerAccess>().Set(
            CallerAccess.From(catalog, [new AccessAssignment("suite", new HashSet<string>(), levels)]), "masking-suite", null);
        var http = factory.Services.GetRequiredService<IHttpContextAccessor>();
        http.HttpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        try
        {
            return JsonSerializer.Serialize(instance, type, options);
        }
        finally
        {
            http.HttpContext = null;
        }
    }

    /// <summary>An instance whose sensitive properties hold distinct canary values; other properties stay default.</summary>
    private static (object Instance, Dictionary<string, string> Canaries) WithCanaries(Type type, List<string> jsonNames, System.Text.Json.Serialization.Metadata.JsonTypeInfo info)
    {
        var instance = RuntimeHelpers.GetUninitializedObject(type);
        var canaries = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var name in jsonNames)
        {
            var property = info.Properties.Single(p => p.Name == name);
            var clr = type.GetProperty(((MemberInfo)property.AttributeProvider!).Name)!;
            var target = Nullable.GetUnderlyingType(clr.PropertyType) ?? clr.PropertyType;
            var number = 7_340_000 + (++index * 1_013);
            object value = target switch
            {
                _ when target == typeof(decimal) => number + 0.37m,
                _ when target == typeof(double) => number + 0.37d,
                _ when target == typeof(long) => (long)number,
                _ when target == typeof(int) => number,
                _ when target == typeof(string) => $"canary-{number}",
                _ => throw new InvalidOperationException($"No canary for {type.Name}.{clr.Name} of type {target}: add one to the masking suite."),
            };
            var backing = type.GetField($"<{clr.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            if (backing is not null)
            {
                backing.SetValue(instance, value);
            }
            else
            {
                clr.SetValue(instance, value);
            }

            canaries[name] = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return (instance, canaries);
    }

    private static IEnumerable<(string Owner, string Property, JsonElement Schema)> Properties()
    {
        foreach (var schema in Contract.GetProperty("components").GetProperty("schemas").EnumerateObject())
        {
            if (schema.Value.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject())
                {
                    yield return (schema.Name, property.Name, property.Value);
                }
            }
        }
    }

    private static IEnumerable<string> Words(string camelCase) => CamelWords().Matches(camelCase).Select(m => m.Value);

    [GeneratedRegex("[A-Za-z][a-z0-9]*")]
    private static partial Regex CamelWords();

    /// <summary>Probe: proves the generated theory really masks (a module type of the host would do the same).</summary>
    public sealed record ProbeQuote(Guid Id, [Sensitive("identity.probe.unit-price")] decimal UnitPrice, [property: Sensitive("identity.probe.wage")] decimal? Wage, string Customer);
}
