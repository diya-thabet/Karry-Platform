using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Modules;
using Karry.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Identity.Tests.Api;

/// <summary>
/// A stand-in business module for the role tests: one site permission and one sensitive field, so field levels set
/// through the role endpoints can be seen in a real response (Identity itself declares no sensitive field).
/// </summary>
public sealed class ProbeModule : IModule
{
    public const string QuotesRead = "probe.quotes.read";
    public const string UnitPrice = "probe.quote.unit-price";

    public string Name => "probe";

    public IReadOnlyList<PermissionDefinition> Permissions =>
        [new(QuotesRead, ScopeKind.Site, privileged: false, DefaultRoles.TenantAdmin, DefaultRoles.Commercial)];

    public IReadOnlyList<SensitiveFieldDefinition> SensitiveFields =>
        [new(UnitPrice, new Dictionary<string, FieldAccess> { [DefaultRoles.TenantAdmin] = FieldAccess.Read, [DefaultRoles.Commercial] = FieldAccess.Read })];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/quote", () => TypedResults.Ok(new ProbeQuote("Q-1", 7_431_517.25m))).RequirePermission(QuotesRead);
}

public sealed record ProbeQuote(string Number, [property: Sensitive(ProbeModule.UnitPrice)] decimal UnitPrice);
