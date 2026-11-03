using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.Api;

/// <summary>
/// The caller's real address behind the hosting provider's load balancer: <c>X-Forwarded-For</c> and
/// <c>X-Forwarded-Proto</c> are believed only from the networks in <c>ReverseProxy:KnownNetworks</c> (CIDR, set per
/// environment in P1.17) and loopback. Anyone else's header is ignored, so a caller cannot pick the address the rate
/// limits count against. Without it every caller behind the load balancer would share its address and one limit.
/// </summary>
public static class ReverseProxy
{
    public const string Section = "ReverseProxy";

    public static IServiceCollection AddKarryReverseProxy(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var networks = configuration.GetSection($"{Section}:KnownNetworks").Get<string[]>() ?? [];
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1; // the load balancer's own entry; addresses further left are the caller's claims
            foreach (var network in networks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });
        return services;
    }

    /// <summary>First in the pipeline, so logs, traces and the rate limits all see the caller's address.</summary>
    public static IApplicationBuilder UseKarryReverseProxy(this IApplicationBuilder app) => app.UseForwardedHeaders();
}
