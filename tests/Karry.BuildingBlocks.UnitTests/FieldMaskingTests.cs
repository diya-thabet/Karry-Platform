using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>
/// Field-level masking through the real module pipeline (US-P1-IAM-07 AC1–AC3, ADR-0022): JSON responses, sensitive
/// input, the OpenAPI marks and exports. Prices are canaries: a masked or hidden value must not appear anywhere in the
/// body, in any form.
/// </summary>
[Trait("Story", "US-P1-IAM-07")]
public sealed class FieldMaskingTests : IAsyncLifetime
{
    internal const string Price = "probe.ticket.unit-price";
    internal const string Margin = "probe.ticket.margin";
    private const decimal PriceCanary = 4817.23m;
    private const decimal MarginCanary = 913.57m;

    private static readonly Guid Tenant = Guid.Parse("01970000-0000-7000-8000-00000000000a");
    private static readonly Guid SiteA = Guid.Parse("01970000-0000-7000-8000-0000000000a1");
    private static readonly Guid SiteB = Guid.Parse("01970000-0000-7000-8000-0000000000b1");

    private static readonly Dictionary<string, IReadOnlyList<AccessAssignment>> Roles = new(StringComparer.Ordinal)
    {
        ["commercial"] = [Levels(FieldAccess.Write, FieldAccess.Write)],
        ["site-manager"] = [Levels(FieldAccess.Read, FieldAccess.Masked)],
        ["weighmaster"] = [Levels(FieldAccess.Masked, FieldAccess.Hidden)],
        ["weighmaster-a-manager-b"] = [Levels(FieldAccess.Masked, FieldAccess.Hidden) with { SiteId = SiteA }, Levels(FieldAccess.Read, FieldAccess.Read) with { SiteId = SiteB }],
    };

    private WebApplication? app;
    private HttpClient? client;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddKarryApiConventions();
        builder.Services.AddKarryTenancy(builder.Configuration);
        builder.Services.AddSingleton<IAccessResolver>(new Resolver());
        builder.Services.AddModules(builder.Configuration, [new ProbeModule()]);
        builder.Services.AddOpenApi("v1", options => options.AddSensitiveFieldMarks());
        app = builder.Build();
        app.UseKarryApiConventions();
        app.Use(FakeSignIn);
        app.UseKarryTenancy();
        app.MapModules();
        app.MapOpenApi("/openapi/{documentName}.json");
        await app.StartAsync();
        client = app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_role_that_reads_the_field_gets_the_value_and_no_masked_list()
    {
        var ticket = (await TicketsAsync("site-manager"))[0];

        ticket.GetProperty("unitPrice").GetDecimal().Should().Be(PriceCanary);
        ticket.GetProperty("maskedFields").EnumerateArray().Select(e => e.GetString()).Should().Equal("margin");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-IAM-005")]
    public async Task A_masked_field_is_absent_and_listed_a_hidden_field_is_absent_and_not_listed()
    {
        var body = await BodyAsync("weighmaster");
        var ticket = JsonDocument.Parse(body).RootElement[0];

        ticket.TryGetProperty("unitPrice", out _).Should().BeFalse();
        ticket.TryGetProperty("margin", out _).Should().BeFalse();
        ticket.GetProperty("maskedFields").EnumerateArray().Select(e => e.GetString()).Should().Equal("unitPrice");
        ticket.GetProperty("customer").GetString().Should().Be("Béton Sahel", "ordinary fields are untouched");
        body.Should().NotContainAny(Canaries);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-IAM-005")]
    public async Task The_level_follows_the_site_of_each_object()
    {
        var tickets = await TicketsAsync("weighmaster-a-manager-b");
        var onA = tickets.Single(t => t.GetProperty("siteId").GetGuid() == SiteA);
        var onB = tickets.Single(t => t.GetProperty("siteId").GetGuid() == SiteB);

        onA.TryGetProperty("unitPrice", out _).Should().BeFalse("weighmaster on site A");
        onB.GetProperty("unitPrice").GetDecimal().Should().Be(PriceCanary, "site manager on site B");
        onB.GetProperty("lines")[0].TryGetProperty("margin", out _).Should().BeFalse("a line without a site counts only company-wide roles");
    }

    [Theory]
    [Trait("TestCase", "TC-P1-IAM-005")]
    [InlineData("unknown-subject")]
    [InlineData(null)]
    public async Task Without_a_role_every_sensitive_field_is_hidden(string? subject)
    {
        var body = await BodyAsync(subject, "/api/v1/probe/public-tickets");

        body.Should().NotContainAny(Canaries).And.NotContain("maskedFields");
    }

    [Fact]
    public async Task Sensitive_fields_of_nested_objects_are_masked_too()
    {
        var body = await BodyAsync("site-manager");
        var line = JsonDocument.Parse(body).RootElement[0].GetProperty("lines")[0];

        line.TryGetProperty("margin", out _).Should().BeFalse();
        line.GetProperty("maskedFields").EnumerateArray().Select(e => e.GetString()).Should().Equal("margin");
        body.Should().NotContain(MarginCanary.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("commercial", """{"siteId":null,"unitPrice":12.5,"lines":[{"margin":3}]}""", 200, null)]
    [InlineData("site-manager", """{"siteId":null,"unitPrice":12.5,"lines":[]}""", 403, Price)]
    [InlineData("site-manager", """{"siteId":null,"lines":[{"margin":3}]}""", 403, Margin)]
    [InlineData("site-manager", """{"siteId":null,"customer":"Béton Sahel","lines":[{}]}""", 200, null)]
    public async Task Setting_a_sensitive_field_needs_the_write_level(string subject, string json, int status, string? field)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/probe/tickets") { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Sub", subject);

        var response = await client!.SendAsync(request, TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(status, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        if (field is not null)
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            problem.GetProperty("code").GetString().Should().Be(FieldMasking.NotWritableCode);
            problem.GetProperty("field").GetString().Should().Be(field);
        }
    }

    [Fact]
    public async Task The_contract_marks_sensitive_properties_and_describes_the_masked_list()
    {
        var document = await client!.GetFromJsonAsync<JsonElement>("/openapi/v1.json", TestContext.Current.CancellationToken);
        var ticket = document.GetProperty("components").GetProperty("schemas").GetProperty("Ticket");
        var properties = ticket.GetProperty("properties");

        properties.GetProperty("unitPrice").GetProperty(FieldMasking.OpenApiExtension).GetString().Should().Be(Price);
        properties.GetProperty("margin").GetProperty(FieldMasking.OpenApiExtension).GetString().Should().Be(Margin, "attribute on the record parameter");
        properties.GetProperty(FieldMasking.MaskedFieldsProperty).GetProperty("type").ToString().Should().Contain("array");
        properties.GetProperty("customer").TryGetProperty(FieldMasking.OpenApiExtension, out _).Should().BeFalse();
        var required = ticket.TryGetProperty("required", out var list) ? list.EnumerateArray().Select(e => e.GetString()).ToList() : [];
        required.Should().NotContain(["unitPrice", "margin"], "a masked field is absent, so it cannot be required");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-IAM-005")]
    public void Exports_leave_out_hidden_columns_and_mask_cells_the_caller_may_not_read()
    {
        var catalog = AuthorizationCatalog.From([new ProbeModule()]);
        var tickets = ProbeModule.Tickets();
        ExportColumn<Ticket>[] columns =
        [
            new("Customer", t => t.Customer),
            new("Unit price", t => t.UnitPrice, Price),
            new("Margin", t => t.Margin, Margin),
        ];
        var mixed = CallerAccess.From(catalog, Roles["weighmaster-a-manager-b"]);
        var weighmaster = CallerAccess.From(catalog, Roles["weighmaster"]);

        string Csv(CallerAccess caller)
        {
            var visible = SensitiveExport.Columns(caller, columns, tickets);
            return string.Join('\n', tickets.Select(t => string.Join(';', visible.Select(c => SensitiveExport.Cell(caller, c, t)))))
                + "\n" + string.Join(';', visible.Select(c => c.Header));
        }

        var weighmasterCsv = Csv(weighmaster);
        weighmasterCsv.Should().NotContain("Margin", "hidden on every row").And.Contain("Unit price").And.Contain(SensitiveExport.MaskedText).And.NotContainAny(Canaries);
        var mixedCsv = Csv(mixed);
        mixedCsv.Split('\n')[0].Should().Contain(SensitiveExport.MaskedText).And.NotContainAny(Canaries);
        mixedCsv.Split('\n')[1].Should().Contain(PriceCanary.ToString(System.Globalization.CultureInfo.CurrentCulture));
    }

    [Fact]
    public void A_type_with_sensitive_fields_cannot_have_its_own_masked_fields_property()
    {
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver().WithAddedModifier(FieldMasking.Modifier(() => CallerAccess.Nobody)),
        };

        var serialize = () => JsonSerializer.Serialize(new Clashing(1m, []), options);

        serialize.Should().Throw<InvalidOperationException>().WithMessage("*maskedFields*");
    }

    private static IEnumerable<string> Canaries =>
    [
        PriceCanary.ToString(System.Globalization.CultureInfo.InvariantCulture),
        MarginCanary.ToString(System.Globalization.CultureInfo.InvariantCulture),
    ];

    private static AccessAssignment Levels(FieldAccess price, FieldAccess margin) =>
        new("custom", new HashSet<string> { "probe.ticket.read" }, new Dictionary<string, FieldAccess> { [Price] = price, [Margin] = margin });

    private static Task FakeSignIn(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Headers["X-Sub"].ToString() is { Length: > 0 } subject)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject), new Claim("tenant_id", Tenant.ToString())], "test"));
        }

        return next(context);
    }

    private async Task<string> BodyAsync(string? subject, string path = "/api/v1/probe/tickets")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (subject is not null)
        {
            request.Headers.Add("X-Sub", subject);
        }

        var response = await client!.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
    }

    private async Task<List<JsonElement>> TicketsAsync(string subject) =>
        [.. JsonDocument.Parse(await BodyAsync(subject)).RootElement.EnumerateArray()];

    public sealed record Line([property: Sensitive(Margin)] decimal Margin);

    public sealed record Ticket(
        Guid Id, Guid? SiteId, string Customer, [property: Sensitive(Price)] decimal UnitPrice, [Sensitive(Margin)] decimal Margin, IReadOnlyList<Line> Lines) : ISiteScoped;

    public sealed record LineInput([property: Sensitive(Margin)] decimal? Margin);

    public sealed record TicketInput(Guid? SiteId, string? Customer, [Sensitive(Price)] decimal? UnitPrice, IReadOnlyList<LineInput>? Lines) : ISiteScoped;

    public sealed record Clashing([property: Sensitive(Price)] decimal UnitPrice, [property: JsonPropertyName("maskedFields")] IReadOnlyList<string> Other);

    private sealed class Resolver : IAccessResolver
    {
        public ValueTask<IReadOnlyList<AccessAssignment>> AssignmentsAsync(TenantId tenant, string subject, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Roles.GetValueOrDefault(subject) ?? []);
    }

    private sealed class ProbeModule : IModule
    {
        public string Name => "probe";

        public IReadOnlyList<PermissionDefinition> Permissions => [new("probe.ticket.read", ScopeKind.Site, privileged: false)];

        public IReadOnlyList<SensitiveFieldDefinition> SensitiveFields =>
        [
            new(Price, new Dictionary<string, FieldAccess>()),
            new(Margin, new Dictionary<string, FieldAccess>()),
        ];

        public static IReadOnlyList<Ticket> Tickets() =>
        [
            new(Guid.CreateVersion7(), SiteA, "Béton Sahel", PriceCanary, MarginCanary, [new Line(MarginCanary)]),
            new(Guid.CreateVersion7(), SiteB, "Béton Sahel", PriceCanary, MarginCanary, [new Line(MarginCanary)]),
        ];

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/tickets", () => TypedResults.Ok(Tickets())).RequirePermission("probe.ticket.read");
            endpoints.MapGet("/public-tickets", () => TypedResults.Ok(Tickets())).AllowAnonymous().AllowWithoutTenant();
            endpoints.MapPost("/tickets", (TicketInput input) => TypedResults.Ok()).RequirePermission("probe.ticket.read");
        }
    }
}
