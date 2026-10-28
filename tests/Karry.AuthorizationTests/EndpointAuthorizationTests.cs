using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Devices;
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
        ["GetCurrentEntitlements"] = "Plan code, state and feature codes of the caller's own company (which features to show or lock); the API checks every call.",
        ["GetCurrentFlags"] = "Feature flag values evaluated for the caller and their own company (which features to show); display only, the API checks every call.",
    };

    /// <summary>
    /// Endpoints for enrolled tablets (ADR-0028 §3): only a device-signed request reaches them (a person's token is refused),
    /// the device is bound to one tenant and one site. Adding one is a security decision: give the reason.
    /// </summary>
    private static readonly Dictionary<string, string> DeviceOnly = new(StringComparer.Ordinal)
    {
        ["GetOwnDevice"] = "The calling tablet's own record, status and settings (looked up by its device ID).",
        ["UnlockTablet"] = "A field user of the tablet's site checks their PIN online (server lockout, ledger event on lock).",
    };

    /// <summary>Endpoints without a tenant: anonymous or for platform operators only.</summary>
    private static readonly Dictionary<string, string> WithoutTenant = new(StringComparer.Ordinal)
    {
        ["ProvisionTenant"] = "Platform operators only (policy platform-operator).",
        ["ListPlans"] = "Platform operators only (policy platform-operator): the plan catalogue is platform data.",
        ["GetPlanVersions"] = "Platform operators only (policy platform-operator): the plan catalogue is platform data.",
        ["PublishPlan"] = "Platform operators only (policy platform-operator): the plan catalogue is platform data.",
        ["GetTenantSubscription"] = "Platform operators only (policy platform-operator): a tenant's subscription and invoices, managed by Karry staff.",
        ["ChangeTenantSubscription"] = "Platform operators only (policy platform-operator): a tenant's subscription and invoices, managed by Karry staff.",
        ["ActOnTenantSubscription"] = "Platform operators only (policy platform-operator): a tenant's subscription and invoices, managed by Karry staff.",
        ["ListTenantInvoices"] = "Platform operators only (policy platform-operator): a tenant's subscription and invoices, managed by Karry staff.",
        ["RecordTenantInvoice"] = "Platform operators only (policy platform-operator): a tenant's subscription and invoices, managed by Karry staff.",
        ["PayTenantInvoice"] = "Platform operators only (policy platform-operator): a tenant's subscription and invoices, managed by Karry staff.",
        ["ListFlags"] = "Platform operators only (policy platform-operator): flag values platform-wide and per tenant, set by Karry staff (ADR-0019).",
        ["SetPlatformFlag"] = "Platform operators only (policy platform-operator): flag values platform-wide and per tenant, set by Karry staff (ADR-0019).",
        ["ListTenantFlags"] = "Platform operators only (policy platform-operator): flag values platform-wide and per tenant, set by Karry staff (ADR-0019).",
        ["SetTenantFlag"] = "Platform operators only (policy platform-operator): flag values platform-wide and per tenant, set by Karry staff (ADR-0019).",
        ["GetTenantBranding"] = "Anonymous login branding (threat model R-07).",
        ["GetTenantLogo"] = "Anonymous login branding (threat model R-07): the company logo, an image only (type read from the content, nosniff).",
        ["AcceptInvitation"] = "Anonymous: the single-use invitation token is the credential and names the tenant.",
        ["EnrollDevice"] = "Anonymous: the single-use 15-minute enrollment code is the credential, looked up only under the company the slug names (ADR-0028).",
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

            if (endpoint.Metadata.GetMetadata<DeviceEndpoint>() is not null)
            {
                if (!DeviceOnly.ContainsKey(name) || permissions.Count > 0 || anyMember)
                {
                    problems.Add($"{name} is a device endpoint but is not in the reviewed list (or also names permissions).");
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
        named.Should().Contain(AnyMember.Keys.Concat(WithoutTenant.Keys).Concat(DeviceOnly.Keys), "a stale review entry suggests a review that did not happen");
    }

    private static IEnumerable<RouteEndpoint> ModuleEndpoints(HostFactory factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true);
}
