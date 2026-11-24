using System.Data.Common;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Sync;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Integrity.Application;
using Karry.Modules.Integrity.Contracts;
using Karry.Modules.Integrity.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Karry.Modules.Integrity.Infrastructure;

/// <summary>
/// Raises and closes flags for the other modules (ADR-0036 §3), joined to their transaction: the fact, its flags and the
/// flags' ledger entries commit together. A rule runs with the company's settings (P2.7 slice 6), else the catalogue's
/// defaults; a security rule always runs.
/// </summary>
internal sealed class FlagWriter(IntegrityDbContext db, ITenantContext tenant, TimeProvider clock) : IIntegrityFlags
{
    // Read once per unit of work and company: an upload asks the same rules for each of its facts.
    private readonly Dictionary<(TenantId?, string), RuleSettings> settings = [];

    public async Task<RuleSettings> SettingsAsync(string rule, CancellationToken cancellationToken)
    {
        var definition = RuleCatalog.Find(rule) ?? throw new ArgumentException($"Unknown rule {rule}.", nameof(rule));
        var key = (tenant.Current, rule);
        if (!settings.TryGetValue(key, out var found))
        {
            var stored = await db.RuleSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Rule == rule, cancellationToken).ConfigureAwait(false);
            found = new RuleSettings(definition.Code, RuleSettingRules.Runs(definition, stored), RuleSettingRules.Effective(definition, stored?.Thresholds));
            settings[key] = found;
        }

        return found;
    }

    public async Task SyncAsync(
        DbConnection connection, DbTransaction transaction, FlagSubject subject, string rule, Guid? siteId, IReadOnlyList<Finding> findings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(findings);
        var definition = RuleCatalog.Find(rule) ?? throw new ArgumentException($"Unknown rule {rule}.", nameof(rule));
        var company = tenant.Current ?? throw new InvalidOperationException("Flags are raised in a tenant.");
        await db.JoinAsync(new Joined(connection, transaction), cancellationToken).ConfigureAwait(false);
        if (!(await SettingsAsync(rule, cancellationToken).ConfigureAwait(false)).Enabled)
        {
            return; // switched off: nothing new is raised, open flags stay open until resolved (ADR-0036 §3)
        }

        var open = await db.Flags
            .Where(f => f.SubjectType == subject.Type && f.SubjectId == subject.Id && f.Rule == rule && f.ClosedAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // A key a person settled is never raised again, though the gap still holds (ADR-0037 §7).
        var settled = await db.Flags
            .Where(f => f.SubjectType == subject.Type && f.SubjectId == subject.Id && f.Rule == rule && f.CloseReason != null && f.CloseReason.StartsWith(SettlementReasons.ClosePrefix))
            .Select(f => f.Key).ToListAsync(cancellationToken).ConfigureAwait(false);
        var found = findings.Where(f => !settled.Contains(f.Key, StringComparer.Ordinal))
            .GroupBy(f => f.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var flag in open.Where(f => !found.ContainsKey(f.Key)))
        {
            flag.Close(clock, IntegrityFlag.Reevaluated, actor: null);
        }

        foreach (var finding in found.Values.Where(f => !open.Any(o => o.Key == f.Key)))
        {
            db.Flags.Add(IntegrityFlag.Open(
                clock, company, rule, definition.Severity, new SubjectRef(subject.Type, subject.Id), finding.Key, finding.SiteId ?? siteId,
                [.. finding.Related.Select(r => new SubjectRef(r.Type, r.Id))], finding.Evidence, finding.Details));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record Joined(DbConnection Connection, DbTransaction Transaction) : ISyncTransaction;
}

internal sealed class RuleSettingStore(IntegrityDbContext db) : IRuleSettingStore
{
    public async Task<IReadOnlyList<RuleSetting>> ListAsync(CancellationToken cancellationToken) =>
        await db.RuleSettings.ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<RuleSetting?> FindAsync(string rule, CancellationToken cancellationToken) =>
        db.RuleSettings.SingleOrDefaultAsync(s => s.Rule == rule, cancellationToken);

    public async Task<bool> TrySaveAsync(RuleSetting setting, CancellationToken cancellationToken)
    {
        if (db.Entry(setting).State == EntityState.Detached)
        {
            db.RuleSettings.Add(setting);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

internal sealed class FlagStore(IntegrityDbContext db) : IFlagStore
{
    public Task<IntegrityFlag?> FindAsync(IntegrityFlagId id, CancellationToken cancellationToken) =>
        db.Flags.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);

    public async Task<IReadOnlyList<IntegrityFlag>> UnsettledGapsAsync(DateTimeOffset openedBy, int take, CancellationToken cancellationToken) =>
        await db.Flags
            .Where(f => f.Rule == IntegrityFlag.ContinuityRule && f.ClosedAt == null && f.FraudSuspectedAt == null && f.OpenedAt <= openedBy)
            .OrderBy(f => f.OpenedAt).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);

    public void Add(IntegrityFlag flag) => db.Flags.Add(flag);

    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

internal sealed class FlagReader(IntegrityDbContext db) : IFlagReader
{
    public async Task<IReadOnlyList<IntegrityFlag>> SearchAsync(FlagQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var flags = db.Flags.AsNoTracking();
        if (query.Sites is { } sites)
        {
            var allowed = sites.ToList();
            flags = flags.Where(f => f.SiteId != null && allowed.Contains(f.SiteId.Value));
        }

        if (query.SiteId is { } site)
        {
            flags = flags.Where(f => f.SiteId == site);
        }

        if (query.Rule is { } rule)
        {
            flags = flags.Where(f => f.Rule == rule);
        }

        if (query.Open is { } open)
        {
            flags = open ? flags.Where(f => f.ClosedAt == null) : flags.Where(f => f.ClosedAt != null);
        }

        if (query.SubjectType is { } type)
        {
            flags = flags.Where(f => f.SubjectType == type);
        }

        if (query.SubjectId is { } subject)
        {
            flags = flags.Where(f => f.SubjectId == subject);
        }

        if (query.After is { } after)
        {
            var cursor = new IntegrityFlagId(after);
            flags = flags.Where(f => f.Id < cursor);
        }

        return await flags.OrderByDescending(f => f.Id).Take(query.Take).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

public static class IntegrityInfrastructure
{
    /// <summary>Flags (ADR-0036): their storage, the contract other modules raise them through, the portal's reading and the rule settings.</summary>
    public static IServiceCollection AddIntegrityInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<IntegrityDbContext>(connectionString);
        services.AddScoped<IIntegrityFlags, FlagWriter>();
        services.AddScoped<IFlagReader, FlagReader>();
        services.AddScoped<FlagReading>();
        services.AddScoped<IFlagStore, FlagStore>();
        services.AddScoped<FlagSettlements>();
        services.AddSingleton<RaiseUnsettledGaps>();
        services.AddScoped<IRuleSettingStore, RuleSettingStore>();
        services.AddScoped<RuleSettingOperations>();
        return services;
    }
}
