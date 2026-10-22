using System.Security.Cryptography;
using Karry.BuildingBlocks.Api;
using Karry.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.BuildingBlocks.Idempotency;

/// <summary>Endpoint metadata: the endpoint requires an <c>Idempotency-Key</c> header.</summary>
public sealed class RequiresIdempotencyKey;

/// <summary>
/// Makes retried requests safe (US-P1-PLT-01, TC-P1-PLT-008). For endpoints marked with
/// <see cref="IdempotencyExtensions.RequireIdempotencyKey"/>:
/// first request with a key → executed, response stored (unless 5xx, which can be retried);
/// same key and same request again → the stored response is replayed (<c>Idempotency-Replayed: true</c>);
/// same key with a different request → 422 <c>idempotency.key_reused</c>;
/// same key while the first is still running → 409 <c>idempotency.request_in_progress</c>.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next, TimeProvider clock, IdempotencyOptions options)
{
    private const string AnonymousScope = "anonymous";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GetEndpoint()?.Metadata.GetMetadata<RequiresIdempotencyKey>() is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var key = context.Request.Headers[IdempotencyOptions.HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > IdempotencyOptions.MaxKeyLength || key.Any(char.IsControl))
        {
            await WriteProblemAsync(context, Error.Validation("idempotency.key_required",
                $"This operation requires an {IdempotencyOptions.HeaderName} header (1-{IdempotencyOptions.MaxKeyLength} printable characters).")).ConfigureAwait(false);
            return;
        }

        var fingerprint = await FingerprintAsync(context.Request).ConfigureAwait(false);
        var db = context.RequestServices.GetRequiredService<PlatformDbContext>();
        var now = clock.GetUtcNow();

        if (!await TryReserveAsync(db, key, fingerprint, now, context.RequestAborted).ConfigureAwait(false))
        {
            await RespondToDuplicateAsync(context, db, key, fingerprint).ConfigureAwait(false);
            return;
        }

        var originalBody = context.Response.Body;
        await using var captured = new MemoryStream();
        context.Response.Body = captured;
        var completed = false;
        try
        {
            await next(context).ConfigureAwait(false);
            completed = context.Response.StatusCode < StatusCodes.Status500InternalServerError;
        }
        finally
        {
            context.Response.Body = originalBody;
            if (completed)
            {
                await db.IdempotencyRecords
                    .Where(r => r.Scope == AnonymousScope && r.Key == key)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(r => r.StatusCode, context.Response.StatusCode)
                        .SetProperty(r => r.ContentType, context.Response.ContentType)
                        .SetProperty(r => r.ResponseBody, captured.ToArray()), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                // Server error or exception: release the key so the client can retry.
                await db.IdempotencyRecords.Where(r => r.Scope == AnonymousScope && r.Key == key)
                    .ExecuteDeleteAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        captured.Position = 0;
        await captured.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task<bool> TryReserveAsync(PlatformDbContext db, string key, string fingerprint, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Expired records, and first requests abandoned by a crashed process, no longer hold their key.
        var abandonedBefore = now - options.InProgressTimeout;
        await db.IdempotencyRecords
            .Where(r => r.Scope == AnonymousScope && r.Key == key && (r.ExpiresAt <= now || (r.StatusCode == null && r.CreatedAt <= abandonedBefore)))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        var expiresAt = now + options.Retention;
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO platform.idempotency_records (scope, key, request_fingerprint, created_at, expires_at)
            VALUES ({AnonymousScope}, {key}, {fingerprint}, {now}, {expiresAt})
            ON CONFLICT (scope, key) DO NOTHING
            """,
            cancellationToken).ConfigureAwait(false);
        return inserted == 1;
    }

    private static async Task RespondToDuplicateAsync(HttpContext context, PlatformDbContext db, string key, string fingerprint)
    {
        var record = await db.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Scope == AnonymousScope && r.Key == key, context.RequestAborted).ConfigureAwait(false);

        if (record is null)
        {
            // The first request failed and released the key in between: ask the client to retry.
            await WriteProblemAsync(context, Error.Conflict("idempotency.request_in_progress", "A request with this key was just processed; retry.")).ConfigureAwait(false);
        }
        else if (!string.Equals(record.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            await WriteProblemAsync(context, Error.BusinessRule("idempotency.key_reused",
                $"This {IdempotencyOptions.HeaderName} was already used for a different request.")).ConfigureAwait(false);
        }
        else if (record.StatusCode is null)
        {
            await WriteProblemAsync(context, Error.Conflict("idempotency.request_in_progress",
                "A request with this key is still being processed; retry later.")).ConfigureAwait(false);
        }
        else
        {
            context.Response.StatusCode = record.StatusCode.Value;
            context.Response.ContentType = record.ContentType;
            context.Response.Headers[IdempotencyOptions.ReplayedHeaderName] = "true";
            await context.Response.Body.WriteAsync(record.ResponseBody ?? [], context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static async Task<string> FingerprintAsync(HttpRequest request)
    {
        request.EnableBuffering();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"{request.Method} {request.Path}{request.QueryString}\n"));
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        request.Body.Position = 0;
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static Task WriteProblemAsync(HttpContext context, Error error) => error.ToProblem().ExecuteAsync(context);
}

public static class IdempotencyExtensions
{
    /// <summary>The endpoint requires an <c>Idempotency-Key</c> header; retries with the same key replay the first response.</summary>
    public static RouteHandlerBuilder RequireIdempotencyKey(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new RequiresIdempotencyKey());

    public static IServiceCollection AddIdempotency(this IServiceCollection services, string connectionString, IdempotencyOptions? options = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options ?? new IdempotencyOptions());
        return Persistence.PersistenceExtensions.AddModuleDbContext<PlatformDbContext>(services, connectionString);
    }

    /// <summary>Must run after routing (it reads endpoint metadata) and before the endpoints.</summary>
    public static IApplicationBuilder UseIdempotency(this IApplicationBuilder app) => app.UseMiddleware<IdempotencyMiddleware>();
}
