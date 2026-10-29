using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>
/// Support sessions for <see cref="TenantResolutionMiddleware"/> (ADR-0029 §4): is this operator's session running,
/// and the ledger entry of each request, written in a scope of its own once the response is known.
/// </summary>
internal sealed partial class SupportSessionAccess(IdentityDbContext db, IServiceScopeFactory scopes, TimeProvider clock, ILogger<SupportSessionAccess> logger)
    : ISupportSessionAccess
{
    public Task<bool> IsActiveAsync(Guid sessionId, string operatorSubject, CancellationToken cancellationToken)
    {
        var id = new SupportSessionId(sessionId);
        var now = clock.GetUtcNow();
        return db.SupportSessions.AsNoTracking()
            .AnyAsync(s => s.Id == id && s.OperatorSubject == operatorSubject && s.EndedAt == null && s.EndsAt > now, cancellationToken);
    }

    public async Task RecordRequestAsync(TenantId tenant, Guid sessionId, string method, string path, int status, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
            var store = scope.ServiceProvider.GetRequiredService<ISupportStore>();
            if (await store.FindSessionAsync(new SupportSessionId(sessionId), cancellationToken).ConfigureAwait(false) is not { } session)
            {
                LogNotRecorded(logger, null, tenant.ToString(), sessionId, method, path, status);
                return;
            }

            session.RecordRequest(clock, method, path, status);
            await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The response is already sent: say loudly that the ledger misses it.
            LogNotRecorded(logger, ex, tenant.ToString(), sessionId, method, path, status);
        }
    }

    [LoggerMessage(EventId = 2205, Level = LogLevel.Error, Message = "Support request {Method} {Path} ({Status}) of session {Session} on tenant {Tenant} was not recorded in the ledger")]
    private static partial void LogNotRecorded(ILogger logger, Exception? exception, string tenant, Guid session, string method, string path, int status);
}
