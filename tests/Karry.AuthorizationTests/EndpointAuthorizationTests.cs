using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.AuthorizationTests;

/// <summary>
/// Every endpoint of the real host carries a reviewed authorization decision (ADR-0022, US-P1-IAM-05/06). The
/// middleware already refuses undeclared tenant endpoints at run time; this test names them at build time, and keeps
/// the list of "any member" and anonymous endpoints under review.
/// </summary>
[Trait("Story", "US-P1-IAM-06")]
public sealed class EndpointAuthorizationTests
{
    /// <summary>Endpoints every user of the tenant may call. Adding one is a security decision: give the reason.</summary>
    private static readonly Dictionary<string, string> AnyMember = new(StringComparer.Ordinal)
    {
        ["GetCurrentUser"] = "The caller's own profile only (looked up by the token's subject).",
        ["GetCurrentAccess"] = "The caller's own permission codes (which screens to show); the API still checks every call.",
        ["GetCurrentTenant"] = "Name and branding of the caller's own company, shown in every app header.",
    };

    /// <summary>Endpoints without a tenant: anonymous or for platform operators only.</summary>
    private static readonly Dictionary<string, string> WithoutTenant = new(StringComparer.Ordinal)
    {
        ["ProvisionTenant"] = "Platform operators only (policy platform-operator).",
        ["GetTenantBranding"] = "Anonymous login branding (threat model R-07).",
        ["AcceptInvitation"] = "Anonymous: the single-use invitation token is the credential and names the tenant.",
    };

    [Fact]
    public void Every_tenant_endpoint_requires_a_declared_permission_or_is_a_reviewed_any_member_endpoint()
    {
        using var factory = new HostFactory();
        var catalog = factory.Services.GetRequiredService<AuthorizationCatalog>();
        var problems = new List<string>();
        var named = new HashSet<string>(StringComparer.Ordinal);

        foreach (var endpoint in ModuleEndpoints(factory))
        {
            var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName ?? "?";
            named.Add(name);
            var permissions = endpoint.Metadata.GetOrderedMetadata<PermissionRequirement>();
            var anyMember = endpoint.Metadata.GetMetadata<AnyTenantMember>() is not null;
            var withoutTenant = endpoint.Metadata.GetMetadata<AllowWithoutTenant>() is not null;

            if (withoutTenant)
            {
                if (!WithoutTenant.ContainsKey(name))
                {
                    problems.Add($"{name} works without a tenant but is not in the reviewed list.");
                }

                continue;
            }

            if (permissions.Count == 0 && !anyMember)
            {
                problems.Add($"{name} declares no permission (it is refused at run time).");
            }

            if (anyMember && (!AnyMember.ContainsKey(name) || permissions.Count > 0))
            {
                problems.Add($"{name} is open to any member but is not in the reviewed list (or also names permissions).");
            }

            problems.AddRange(permissions.Where(p => catalog.Permission(p.Permission) is null).Select(p => $"{name} requires undeclared permission {p.Permission}."));
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                problems.Add($"{name} allows anonymous callers but requires a tenant.");
            }
        }

        problems.Should().BeEmpty();
        named.Should().Contain(AnyMember.Keys.Concat(WithoutTenant.Keys), "a stale review entry suggests a review that did not happen");
    }

    private static IEnumerable<RouteEndpoint> ModuleEndpoints(HostFactory factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true);
}
