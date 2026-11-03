using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Karry.BuildingBlocks.Api;

/// <summary>
/// Security headers on every answer of the API, errors included (hardening plan H-W2). The API serves JSON and images
/// only, never a page: nothing it returns may run code, be framed, be sniffed into another type, send a referrer or be
/// kept by a shared cache. An endpoint that sets one of these headers itself (an image with its own caching) keeps
/// its value. The portal and field app send their own page policy (<c>src/frontend/security-headers.ts</c>).
/// </summary>
public static class SecurityHeaders
{
    /// <summary>Nothing an API answer contains may load, run or be framed.</summary>
    public const string ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    /// <summary>One year, subdomains included (every Karry host is HTTPS-only behind the ingress).</summary>
    public const string StrictTransportSecurity = "max-age=31536000; includeSubDomains";

    /// <summary>
    /// Adds the headers when the response starts. HSTS only on HTTPS answers outside Development (the scheme comes from
    /// the trusted proxy, <see cref="ReverseProxy"/>); a browser must never pin HSTS for localhost.
    /// </summary>
    public static IApplicationBuilder UseKarrySecurityHeaders(this IApplicationBuilder app)
    {
        var development = app.ApplicationServices.GetRequiredService<IHostEnvironment>().IsDevelopment();
        return app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                if (string.IsNullOrEmpty(headers.ContentSecurityPolicy))
                {
                    headers.ContentSecurityPolicy = ContentSecurityPolicy;
                }

                if (string.IsNullOrEmpty(headers.CacheControl))
                {
                    headers.CacheControl = "no-store";
                }

                if (context.Request.IsHttps && !development)
                {
                    headers.StrictTransportSecurity = StrictTransportSecurity;
                }

                return Task.CompletedTask;
            });
            return next(context);
        });
    }

    /// <summary>Kestrel does not announce itself (<c>Server: Kestrel</c> only helps an attacker pick exploits).</summary>
    public static IServiceCollection AddKarrySecurityHeaders(this IServiceCollection services) =>
        services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options => options.AddServerHeader = false);
}
