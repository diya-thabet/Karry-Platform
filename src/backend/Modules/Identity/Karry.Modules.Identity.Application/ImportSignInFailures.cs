using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Identity.Application;

/// <summary>A failed sign-in event as Keycloak reports it (<c>LOGIN_ERROR</c>).</summary>
public sealed record KeycloakSignInEvent(Guid Id, DateTimeOffset Time, string? Error, string? Subject, string? IpAddress);

/// <summary>Keycloak's event store (admin API, events kept 30 days by the realm).</summary>
public interface ISignInEventSource
{
    /// <summary>Failed sign-ins at or after <paramref name="from"/>, oldest first, skipping <paramref name="first"/>, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<KeycloakSignInEvent>> ReadFailuresAsync(DateTimeOffset from, int first, int max, CancellationToken cancellationToken);

    /// <summary>The tenant of a Keycloak account (its admin-only <c>tenant_id</c> attribute), or null.</summary>
    Task<TenantId?> TenantOfAsync(string subject, CancellationToken cancellationToken);
}

/// <summary>Where an import stopped (platform data: one row per source, no tenant).</summary>
public interface IImportCursor
{
    Task<DateTimeOffset?> GetAsync(string source, CancellationToken cancellationToken);

    Task SetAsync(string source, DateTimeOffset position, CancellationToken cancellationToken);
}

public sealed record SignInImportRun(int Imported, int AlreadyImported, int Unattributed);

/// <summary>
/// Copies Keycloak's failed sign-ins of known users into their tenant's ledger (US-P1-IAM-01 AC4): wrong passwords,
/// wrong second factors, and attempts refused because the brute-force protection locked the account. Each event is
/// recorded under its own tenant, in its own unit of work, once (Keycloak's event ID is the key). Failures for unknown
/// usernames belong to no tenant and are only counted. Runs every minute in <c>Karry.Workers</c>.
/// </summary>
public sealed class ImportSignInFailures(IServiceScopeFactory scopes, ISignInEventSource source, TimeProvider clock)
{
    public const string CursorName = "keycloak.sign_in_failures";
    public const int PageSize = 200;

    /// <summary>First run: everything Keycloak still keeps (the realm's 30-day event expiry).</summary>
    public static readonly TimeSpan FirstRunLookBack = TimeSpan.FromDays(30);

    public async Task<SignInImportRun> RunAsync(CancellationToken cancellationToken)
    {
        await using var platform = scopes.CreateAsyncScope();
        var cursor = platform.ServiceProvider.GetRequiredService<IImportCursor>();
        // Each read starts 1 ms before the last event seen: whatever Keycloak's boundary semantics, an event in that
        // millisecond is never skipped; the ones already recorded are recognized by their ID.
        var position = await cursor.GetAsync(CursorName, cancellationToken).ConfigureAwait(false);
        var from = position is { } seen ? seen.AddMilliseconds(-1) : clock.GetUtcNow() - FirstRunLookBack;
        var tenants = new Dictionary<string, TenantId?>(StringComparer.Ordinal);
        int imported = 0, duplicates = 0, unattributed = 0;

        // Offset pages over a fixed start: Keycloak returns events oldest first and new ones only append at the end,
        // so pages are stable, even when more events than a page share one millisecond.
        for (var first = 0; ; first += PageSize)
        {
            var page = await source.ReadFailuresAsync(from, first, PageSize, cancellationToken).ConfigureAwait(false);
            foreach (var failure in page)
            {
                var outcome = await RecordAsync(failure, tenants, cancellationToken).ConfigureAwait(false);
                imported += outcome == Outcome.Imported ? 1 : 0;
                duplicates += outcome == Outcome.Duplicate ? 1 : 0;
                unattributed += outcome == Outcome.Unattributed ? 1 : 0;
            }

            if (page.Count > 0)
            {
                await cursor.SetAsync(CursorName, page.Max(e => e.Time), cancellationToken).ConfigureAwait(false);
            }

            if (page.Count < PageSize)
            {
                break;
            }
        }

        return new SignInImportRun(imported, duplicates, unattributed);
    }

    private enum Outcome
    {
        Imported,
        Duplicate,
        Unattributed,
    }

    private async Task<Outcome> RecordAsync(KeycloakSignInEvent failure, Dictionary<string, TenantId?> tenants, CancellationToken cancellationToken)
    {
        if (failure.Subject is not { Length: > 0 } subject)
        {
            return Outcome.Unattributed;
        }

        if (!tenants.TryGetValue(subject, out var tenant))
        {
            tenant = await source.TenantOfAsync(subject, cancellationToken).ConfigureAwait(false);
            tenants[subject] = tenant;
        }

        if (tenant is not { } owner)
        {
            return Outcome.Unattributed;
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(owner);
        var store = scope.ServiceProvider.GetRequiredService<IIdentityStore>();
        var user = await store.FindBySubjectAsync(subject, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Outcome.Unattributed;
        }

        var recorded = await store.AddSignInFailureAsync(
            SignInFailure.Record(failure.Id, user, failure.Error, failure.IpAddress, failure.Time), cancellationToken).ConfigureAwait(false);
        return recorded ? Outcome.Imported : Outcome.Duplicate;
    }
}
