using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.Entitlements;

/// <summary>What an endpoint is, for the subscription-state rules (ADR-0023). Endpoints without a declaration are office work.</summary>
public enum SubscriptionAccess
{
    Office = 0,

    /// <summary>Field capture and sync: keeps working while a subscription is restricted (field work never stops first).</summary>
    FieldCapture = 1,

    /// <summary>The company's plan, usage and invoices: always reachable, so it can see why and pay.</summary>
    Billing = 2,

    /// <summary>Getting the company's data out (exports, integrity proof): always reachable, the data is theirs.</summary>
    Export = 3,

    /// <summary>The signed-in person's own profile and access: readable in every state (the apps need it to start).</summary>
    Account = 4,
}

/// <summary>Endpoint metadata: the kind of work, for <see cref="SubscriptionStateMiddleware"/>.</summary>
public sealed record SubscriptionAccessKind(SubscriptionAccess Access);

public static class SubscriptionAccessExtensions
{
    public static TBuilder FieldCapture<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new SubscriptionAccessKind(SubscriptionAccess.FieldCapture));

    public static TBuilder BillingAccess<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new SubscriptionAccessKind(SubscriptionAccess.Billing));

    public static TBuilder ExportAccess<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new SubscriptionAccessKind(SubscriptionAccess.Export));

    public static TBuilder AccountAccess<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new SubscriptionAccessKind(SubscriptionAccess.Account));
}

/// <summary>
/// The state table of ADR-0023 (US-P1-BIL-05): which work a subscription state still allows. Reads are GET and HEAD.
/// <list type="bullet">
/// <item>Trial, Active, PastDue: everything (PastDue shows a banner).</item>
/// <item>Restricted: reads; writes only for field capture, billing and export.</item>
/// <item>Suspended: reads; writes only for billing and export (field capture stops).</item>
/// <item>Cancelled, Expired, Deleted (and any unknown state): billing, export and the person's own account only.</item>
/// </list>
/// </summary>
public static class SubscriptionRules
{
    public const string RestrictedCode = "billing.subscription_restricted";
    public const string SuspendedCode = "billing.subscription_suspended";
    public const string InactiveCode = "billing.subscription_inactive";

    /// <summary>Null when allowed, else the refusal code.</summary>
    public static string? Refusal(string status, SubscriptionAccess access, bool read) => status switch
    {
        "trial" or "active" or "past_due" => null,
        "restricted" => read || access is SubscriptionAccess.FieldCapture or SubscriptionAccess.Billing or SubscriptionAccess.Export ? null : RestrictedCode,
        "suspended" => read || access is SubscriptionAccess.Billing or SubscriptionAccess.Export ? null : SuspendedCode,
        _ => access is SubscriptionAccess.Billing or SubscriptionAccess.Export || (read && access == SubscriptionAccess.Account) ? null : InactiveCode,
    };

    public static bool IsRead(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
}

/// <summary>
/// Enforces the subscription state on tenant endpoints (US-P1-BIL-05), from the cached entitlements: after the
/// permission and plan checks, before the endpoint. Anonymous and platform-operator endpoints (no tenant) are not
/// subject to a tenant's state. Without a resolver (a host without Billing, some module tests) nothing is enforced;
/// the API host always has one (asserted by its integration tests).
/// </summary>
public sealed class SubscriptionStateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.GetEndpoint();
        var services = context.RequestServices;
        if (endpoint is null || endpoint.Metadata.GetMetadata<AllowWithoutTenant>() is not null
            || services.GetService<ITenantContext>()?.Current is not { } tenant || services.GetService<IEntitlementResolver>() is not { } resolver)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var status = (await resolver.ResolveAsync(tenant, context.RequestAborted).ConfigureAwait(false)).Status;
        var access = endpoint.Metadata.GetMetadata<SubscriptionAccessKind>()?.Access ?? SubscriptionAccess.Office;
        if (SubscriptionRules.Refusal(status, access, SubscriptionRules.IsRead(context.Request.Method)) is { } code)
        {
            await Error.Forbidden(code, Message(code)).With("subscriptionStatus", status).ToProblem().ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private static string Message(string code) => code switch
    {
        SubscriptionRules.RestrictedCode => "Your company's subscription is restricted until it is paid: office changes are paused; field capture continues.",
        SubscriptionRules.SuspendedCode => "Your company's subscription is suspended until it is paid: everything is read-only; export stays available.",
        _ => "Your company's subscription has ended: only billing and export are available.",
    };
}
