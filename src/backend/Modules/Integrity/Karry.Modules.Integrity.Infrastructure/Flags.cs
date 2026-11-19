using System.Data.Common;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Sync;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Integrity.Application;
using Karry.Modules.Integrity.Contracts;
using Karry.Modules.Integrity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Integrity.Infrastructure;

/// <summary>
/// Raises and closes flags for the other modules (ADR-0036 §3), joined to their transaction: the fact, its flags and the
/// flags' ledger entries commit together. Settings are the catalogue's defaults until P2.7 slice 6 lets a company tune them.
/// </summary>
internal sealed class FlagWriter(IntegrityDbContext db, ITenantContext tenant, TimeProvider clock) : IIntegrityFlags
{
    public Task<RuleSettings> SettingsAsync(string rule, CancellationToken cancellationToken)
    {
        var definition = RuleCatalog.Find(rule) ?? throw new ArgumentException($"Unknown rule {rule}.", nameof(rule));
        return Task.FromResult(new RuleSettings(definition.Code, Enabled: true, definition.Thresholds));
    }

    public async Task SyncAsync(
        DbConnection connection, DbTransaction transaction, FlagSubject subject, string rule, Guid? siteId, IReadOnlyList<Finding> findings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(findings);
        var definition = RuleCatalog.Find(rule) ?? throw new ArgumentException($"Unknown rule {rule}.", nameof(rule));
        if (!(await SettingsAsync(rule, cancellationToken).ConfigureAwait(false)).Enabled)
        {
            return;
        }

        var company = tenant.Current ?? throw new InvalidOperationException("Flags are raised in a tenant.");
        await db.JoinAsync(new Joined(connection, transaction), cancellationToken).ConfigureAwait(false);
        var open = await db.Flags
            .Where(f => f.SubjectType == subject.Type && f.SubjectId == subject.Id && f.Rule == rule && f.ClosedAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var found = findings.GroupBy(f => f.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
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
    /// <summary>Flags (ADR-0036): their storage, the contract other modules raise them through, and the portal's reading.</summary>
    public static IServiceCollection AddIntegrityInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<IntegrityDbContext>(connectionString);
        services.AddScoped<IIntegrityFlags, FlagWriter>();
        services.AddScoped<IFlagReader, FlagReader>();
        services.AddScoped<FlagReading>();
        return services;
    }
}
