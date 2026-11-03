using System.Diagnostics.Metrics;
using System.Globalization;
using System.Threading.RateLimiting;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Karry.BuildingBlocks.Api;

/// <summary>
/// Request limits per minute (threat T-12, <c>docs/02-architecture/api-standards.md</c>). Counted in memory by each API
/// instance: with several instances a caller gets up to that many times the limit, which is accepted for M1.
/// </summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    /// <summary>False only in test hosts that send many requests as one caller; refused in production.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Every request from one IP address, checked before authentication (bounds floods of bad tokens).</summary>
    public int PerAddressPerMinute { get; set; } = 6000;

    /// <summary>Requests without a signed-in caller (enrolment, public branding) from one IP address.</summary>
    public int AnonymousPerAddressPerMinute { get; set; } = 60;

    /// <summary>Requests of one enrolled tablet.</summary>
    public int PerDevicePerMinute { get; set; } = 120;

    /// <summary>Requests of one person (portal, field sign-in).</summary>
    public int PerPersonPerMinute { get; set; } = 600;

    /// <summary>Requests of all callers of one company together (one company cannot starve the others).</summary>
    public int PerTenantPerMinute { get; set; } = 12000;
}

/// <summary>
/// Two checks: by IP address before authentication, then by caller (tablet, person, or IP address when anonymous) and
/// by company after authorization, when the caller is proven. A refused request gets <c>429 request.rate_limited</c>
/// with <c>Retry-After</c>. Health probes are never limited.
/// </summary>
public static class RateLimits
{
    public const string Code = "request.rate_limited";

    private static readonly Meter Meter = new("Karry.Api");

    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>("karry.rate_limit.rejected", description: "Requests refused with 429, by limit");

    public static IServiceCollection AddKarryRateLimits(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var options = configuration.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();
        if (!options.Enabled && environment.IsProduction())
        {
            throw new InvalidOperationException("RateLimits:Enabled cannot be false in production.");
        }

        services.AddSingleton(Options.Create(options));
        services.AddSingleton<RateLimiters>();
        return services;
    }

    /// <summary>The address check; a no-op when <see cref="AddKarryRateLimits"/> was not called (module test hosts).</summary>
    internal static IApplicationBuilder UseAddressRateLimit(this IApplicationBuilder app) =>
        app.Use((context, next) => CheckAsync(context, next, (limiters => limiters.Address, _ => "address")));

    /// <summary>The caller and company checks, after authorization.</summary>
    internal static IApplicationBuilder UseCallerRateLimit(this IApplicationBuilder app) =>
        app.Use((context, next) => CheckAsync(context, next, (limiters => limiters.Caller, context => RateLimiters.CallerOf(context).Kind), (limiters => limiters.Tenant, _ => "tenant")));

    private static async Task CheckAsync(HttpContext context, RequestDelegate next, params (Func<RateLimiters, PartitionedRateLimiter<HttpContext>> Limiter, Func<HttpContext, string> Kind)[] checks)
    {
        var limiters = context.RequestServices.GetService<RateLimiters>();
        if (limiters is null || !limiters.Enabled || context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        foreach (var check in checks)
        {
            using var lease = check.Limiter(limiters).AttemptAcquire(context);
            if (!lease.IsAcquired)
            {
                var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromMinutes(1);
                var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                Rejected.Add(1, new KeyValuePair<string, object?>("limit", check.Kind(context))); // the kind, never the caller's ID
                context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                await TypedResults.Problem(
                    title: "Too many requests. Wait before trying again.",
                    statusCode: StatusCodes.Status429TooManyRequests,
                    type: ProblemTypes.For(Code).ToString(),
                    extensions: new Dictionary<string, object?> { ["code"] = Code, ["retryAfterSeconds"] = seconds }).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    private sealed class RateLimiters : IDisposable
    {
        public RateLimiters(IOptions<RateLimitOptions> options)
        {
            var limits = options.Value;
            Enabled = limits.Enabled;
            Address = PartitionedRateLimiter.Create<HttpContext, string>(context => Window("address:" + AddressOf(context), limits.PerAddressPerMinute));
            Caller = PartitionedRateLimiter.Create<HttpContext, string>(context => CallerOf(context) switch
            {
                ("device", var id) => Window("device:" + id, limits.PerDevicePerMinute),
                ("person", var id) => Window("person:" + id, limits.PerPersonPerMinute),
                (_, var address) => Window("anonymous:" + address, limits.AnonymousPerAddressPerMinute),
            });
            Tenant = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.User.FindFirst(TenantResolutionMiddleware.TenantClaim)?.Value is { Length: > 0 } tenant
                    ? Window("tenant:" + tenant, limits.PerTenantPerMinute)
                    : RateLimitPartition.GetNoLimiter(string.Empty));
        }

        public bool Enabled { get; }

        public PartitionedRateLimiter<HttpContext> Address { get; }

        public PartitionedRateLimiter<HttpContext> Caller { get; }

        public PartitionedRateLimiter<HttpContext> Tenant { get; }

        public void Dispose()
        {
            Address.Dispose();
            Caller.Dispose();
            Tenant.Dispose();
        }

        /// <summary>Proven callers only: the device claim is set by the Device scheme after the signature checked.</summary>
        public static (string Kind, string Id) CallerOf(HttpContext context)
        {
            var user = context.User;
            if (user.FindFirst(DeviceAuthenticationHandler.DeviceIdClaim)?.Value is { Length: > 0 } device)
            {
                return ("device", device);
            }

            return user.Identity?.IsAuthenticated == true && user.FindFirst("sub")?.Value is { Length: > 0 } subject
                ? ("person", subject)
                : ("anonymous", AddressOf(context));
        }

        private static string AddressOf(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Six segments of ten seconds: a caller at the limit gets permits back every ten seconds, not all at once.
        private static RateLimitPartition<string> Window(string key, int permitsPerMinute) =>
            RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    }
}
