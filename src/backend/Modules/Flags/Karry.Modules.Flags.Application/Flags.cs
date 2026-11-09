using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Flags.Domain;
using Karry.SharedKernel.Flags;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Flags.Application;

/// <summary>Platform-wide values and their history (global tables), and the current tenant's overrides (RLS).</summary>
public interface IFlagStore
{
    Task<IReadOnlyList<PlatformFlag>> PlatformAsync(CancellationToken cancellationToken);

    Task<PlatformFlag?> FindPlatformAsync(string key, CancellationToken cancellationToken);

    void Add(PlatformFlag flag);

    void Add(PlatformFlagChange change);

    Task<IReadOnlyList<TenantFlag>> TenantAsync(CancellationToken cancellationToken);

    Task<TenantFlag?> FindTenantAsync(string key, string subject, CancellationToken cancellationToken);

    void Add(TenantFlag flag);

    /// <summary>False when another operator stored the same flag first (its first value is a new row): nothing is saved.</summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);
}

/// <summary>The evaluation cache: a change on this instance forgets at once (others within 60 s).</summary>
public interface IFlagCache
{
    /// <summary>Forgets one tenant's overrides, or everything (a platform-wide change) when <paramref name="tenant"/> is null.</summary>
    void Forget(TenantId? tenant);
}

public sealed record SetFlag(bool? Enabled, string? UserSubject);

/// <summary>
/// Flag changes by platform operators (US-P1-PLT-05 AC2): effective without a release; a company's or user's override
/// is in that company's ledger, a platform-wide value in the append-only platform history.
/// </summary>
public sealed class FlagOperations(IFlagStore store, IFlagCache cache, TimeProvider clock, Microsoft.Extensions.Configuration.IConfiguration configuration)
{
    public const string EnvironmentSection = "Flags:Environment";

    /// <summary>The value this environment's configuration gives a flag (<c>Flags:Environment:&lt;key&gt;</c>), if any.</summary>
    public bool? EnvironmentValue(FlagDefinition flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        return bool.TryParse(configuration[$"{EnvironmentSection}:{flag.Key}"], out var value) ? value : null;
    }

    public async Task<Result<PlatformFlag>> SetPlatformAsync(string key, SetFlag request, string? actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (FlagRules.Validate(key, null) is { } invalid)
        {
            return invalid;
        }

        if (!string.IsNullOrWhiteSpace(request.UserSubject))
        {
            return Error.Validation("flags.subject_not_allowed", "A platform-wide value has no user; set it on a company instead.");
        }

        var flag = await store.FindPlatformAsync(key, cancellationToken).ConfigureAwait(false);
        if (flag is null)
        {
            flag = PlatformFlag.For(key);
            store.Add(flag);
        }

        if (flag.Change(clock, request.Enabled, actor) is { } change)
        {
            store.Add(change);
            if (!await store.TrySaveAsync(cancellationToken).ConfigureAwait(false))
            {
                return RecordVersions.Conflict; // the same flag set by two operators at once (hardening plan H-C1)
            }

            cache.Forget(null);
        }

        return flag;
    }

    /// <summary>In the addressed tenant's unit of work (the caller sets the tenant context).</summary>
    public async Task<Result<TenantFlag>> SetTenantAsync(TenantId tenant, string key, SetFlag request, string? actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (FlagRules.Validate(key, request.UserSubject) is { } invalid)
        {
            return invalid;
        }

        var subject = request.UserSubject?.Trim() ?? string.Empty;
        var flag = await store.FindTenantAsync(key, subject, cancellationToken).ConfigureAwait(false);
        if (flag is null)
        {
            var created = TenantFlag.Create(clock, tenant, key, subject, request.Enabled, actor);
            if (!created.IsSuccess)
            {
                return created.Error;
            }

            flag = created.Value;
            store.Add(flag);
        }
        else if (!flag.Change(clock, request.Enabled, actor))
        {
            return flag;
        }

        if (!await store.TrySaveAsync(cancellationToken).ConfigureAwait(false))
        {
            return RecordVersions.Conflict; // the same override set by two operators at once (hardening plan H-C1)
        }

        cache.Forget(tenant);
        return flag;
    }
}
