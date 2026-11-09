using System.Diagnostics.Metrics;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Organization.Domain;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Organization.Application;

public sealed record ShiftInput(string? Code, string? Name, string? Start, string? End, IReadOnlyList<string>? Days);

public sealed record ShiftPatternInput(IReadOnlyList<ShiftInput>? Shifts)
{
    public Result<IReadOnlyList<ShiftDefinition>> Validate() =>
        ShiftRules.Validate(Shifts?.Select(s => (s?.Code, s?.Name, s?.Start, s?.End, s?.Days)).ToList());
}

public sealed record NonWorkingDayInput(string? Kind, string? Name);

/// <summary>Persistence port of shift patterns, non-working days and stored instances (the caller's unit of work, RLS).</summary>
public interface IShiftStore
{
    Task<IReadOnlyList<ShiftPattern>> PatternsAsync(SiteId site, CancellationToken cancellationToken);

    Task<IReadOnlyList<NonWorkingDay>> NonWorkingDaysAsync(SiteId site, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    Task<IReadOnlyList<ShiftInstance>> InstancesAsync(SiteId site, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    void Add(ShiftPattern pattern);

    void Remove(ShiftPattern pattern);

    void Add(NonWorkingDay day);

    void Remove(NonWorkingDay day);

    void Add(ShiftInstance instance);

    void Remove(ShiftInstance instance);

    /// <summary>
    /// Saves the unit of work; false when a concurrent change of the same site's shifts was saved first (the same date
    /// added twice, or the same instances replaced twice): nothing is saved and the caller answers 409.
    /// </summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);
}

/// <summary>The shift of a site an instant belongs to, for other modules (ADR-0034 §2).</summary>
public sealed class ShiftResolution(ISiteStore sites, IShiftStore store) : IShiftCalendar
{
    public async Task<ResolvedShift?> ResolveAsync(Guid siteId, DateTimeOffset instant, CancellationToken cancellationToken)
    {
        if (await sites.FindAsync(new SiteId(siteId), cancellationToken).ConfigureAwait(false) is not { } site)
        {
            return null;
        }

        var patterns = await store.PatternsAsync(site.Id, cancellationToken).ConfigureAwait(false);
        var today = ShiftCalendar.Today(site.TimeZone, instant);
        var days = await store.NonWorkingDaysAsync(site.Id, today.AddDays(-1), today.AddDays(1), cancellationToken).ConfigureAwait(false);
        var shift = ShiftCalendar.Resolve(site.TimeZone, [.. patterns.Select(p => (p.EffectiveFrom, p.Shifts))], days.Select(d => d.Date).ToHashSet(), instant);
        return shift is null ? null : new ResolvedShift(shift.Date, shift.Code, shift.Name, shift.StartsAt, shift.EndsAt, shift.TimeZone);
    }
}

/// <summary>What one reconciliation of stored instances changed.</summary>
public readonly record struct InstanceChanges(int Added, int Changed, int Removed)
{
    public static InstanceChanges operator +(InstanceChanges a, InstanceChanges b) => new(a.Added + b.Added, a.Changed + b.Changed, a.Removed + b.Removed);

    public static InstanceChanges Add(InstanceChanges a, InstanceChanges b) => a + b;
}

/// <summary>
/// Shift instances of a site (US-P1-ORG-02 AC3, ADR-0024): computed from the pattern versions and non-working days, and
/// the stored ones of the next <see cref="ShiftCalendar.StoredDays"/> days kept equal to the computed ones. An instance
/// that has started is never changed or removed; an archived site keeps no future instances.
/// </summary>
public sealed class ShiftInstances(IShiftStore store, TimeProvider clock)
{
    public async Task<IReadOnlyList<ComputedShift>> ComputeAsync(Site site, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(site);
        var patterns = await store.PatternsAsync(site.Id, cancellationToken).ConfigureAwait(false);
        var days = await store.NonWorkingDaysAsync(site.Id, from, to, cancellationToken).ConfigureAwait(false);
        return ShiftCalendar.Compute(site.TimeZone, [.. patterns.Select(p => (p.EffectiveFrom, p.Shifts))], days.Select(d => d.Date).ToHashSet(), from, to);
    }

    /// <summary>Brings the stored instances of the next days in line with the rules; the caller saves.</summary>
    public async Task<InstanceChanges> ReconcileAsync(Site site, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(site);
        var now = clock.GetUtcNow();
        var today = ShiftCalendar.Today(site.TimeZone, now);
        var to = today.AddDays(ShiftCalendar.StoredDays - 1);
        var computed = site.Status == SiteStatus.Active ? await ComputeAsync(site, today, to, cancellationToken).ConfigureAwait(false) : [];
        var stored = await store.InstancesAsync(site.Id, today, to, cancellationToken).ConfigureAwait(false);

        var changes = default(InstanceChanges);
        foreach (var instance in stored.Where(i => i.StartsAt > now))
        {
            var wanted = computed.FirstOrDefault(c => c.Date == instance.Date && c.Code == instance.ShiftCode);
            if (wanted is null)
            {
                store.Remove(instance);
                changes += new InstanceChanges(0, 0, 1);
            }
            else if (!instance.Matches(wanted))
            {
                instance.Apply(wanted);
                changes += new InstanceChanges(0, 1, 0);
            }
        }

        foreach (var shift in computed.Where(c => !stored.Any(i => i.Date == c.Date && i.ShiftCode == c.Code)))
        {
            store.Add(ShiftInstance.From(clock, site.TenantId, site.Id, shift));
            changes += new InstanceChanges(1, 0, 0);
        }

        return changes;
    }
}

/// <summary>
/// US-P1-ORG-02: a site's shift pattern versions and non-working days, each change in the company's ledger and applied
/// to the stored instances in the same transaction. Only dates after the site's today change (never history).
/// </summary>
public sealed class ShiftOperations(IShiftStore store, SiteOperations sites, ShiftInstances instances, ICallerAccess caller, TimeProvider clock)
{
    public static readonly Error PatternNotFound = Error.NotFound("organization.shift_pattern_not_found", "No pattern version starts on this date.");

    public static readonly Error DayNotFound = Error.NotFound("organization.non_working_day_not_found", "This date is not a non-working day of the site.");

    public static readonly Error SiteArchived = Error.Conflict("organization.site_archived", "An archived site has no shifts; restore it first.");

    public static readonly Error NotManaged = Error.Forbidden("organization.site_not_managed", "You cannot change the shifts of this site.");

    public static Error DateInPast(DateOnly today) =>
        Error.Validation("organization.date_in_past", "Only dates after the site's today can change; past shifts are never rewritten.")
            .With("today", today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    public static readonly Error RangeInvalid = Error.Validation(
        "organization.range_invalid", $"The range runs forward and covers at most {ShiftCalendar.MaxComputedDays} days.");

    public async Task<Result<IReadOnlyList<ShiftPattern>>> PatternsAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        var site = await sites.GetAsync(siteId, cancellationToken).ConfigureAwait(false);
        return site.IsSuccess
            ? Result.Success<IReadOnlyList<ShiftPattern>>([.. (await store.PatternsAsync(siteId, cancellationToken).ConfigureAwait(false)).OrderBy(p => p.EffectiveFrom)])
            : site.Error;
    }

    /// <summary>
    /// Sets the version starting on <paramref name="effectiveFrom"/>: a date after the site's today, or any date for the
    /// site's first pattern. A version not yet in effect is replaced.
    /// </summary>
    public async Task<Result<ShiftPattern>> SetPatternAsync(SiteId siteId, DateOnly effectiveFrom, ShiftPatternInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var site = await ManagedSiteAsync(siteId, cancellationToken).ConfigureAwait(false);
        if (!site.IsSuccess)
        {
            return site.Error;
        }

        var shifts = input.Validate();
        if (!shifts.IsSuccess)
        {
            return shifts.Error;
        }

        var patterns = await store.PatternsAsync(siteId, cancellationToken).ConfigureAwait(false);
        var today = ShiftCalendar.Today(site.Value.TimeZone, clock.GetUtcNow());
        if (patterns.Count > 0 && effectiveFrom <= today)
        {
            return DateInPast(today);
        }

        var pattern = patterns.SingleOrDefault(p => p.EffectiveFrom == effectiveFrom);
        if (pattern is null)
        {
            pattern = ShiftPattern.Create(clock, site.Value.TenantId, siteId, effectiveFrom, shifts.Value, caller.Subject);
            store.Add(pattern);
        }
        else if (!pattern.Change(clock, shifts.Value, caller.Subject))
        {
            return pattern;
        }

        return await ReconcileAndSaveAsync(site.Value, cancellationToken).ConfigureAwait(false) ? pattern : RecordVersions.Conflict;
    }

    /// <summary>Removes a version not yet in effect.</summary>
    public async Task<Result<bool>> RemovePatternAsync(SiteId siteId, DateOnly effectiveFrom, CancellationToken cancellationToken)
    {
        var site = await ManagedSiteAsync(siteId, cancellationToken).ConfigureAwait(false);
        if (!site.IsSuccess)
        {
            return site.Error;
        }

        var pattern = (await store.PatternsAsync(siteId, cancellationToken).ConfigureAwait(false)).SingleOrDefault(p => p.EffectiveFrom == effectiveFrom);
        if (pattern is null)
        {
            return PatternNotFound;
        }

        var today = ShiftCalendar.Today(site.Value.TimeZone, clock.GetUtcNow());
        if (effectiveFrom <= today)
        {
            return DateInPast(today);
        }

        pattern.Remove(clock, caller.Subject);
        store.Remove(pattern);
        return await ReconcileAndSaveAsync(site.Value, cancellationToken).ConfigureAwait(false) ? true : RecordVersions.Conflict;
    }

    /// <summary>The non-working days of a range (by default the year from the site's today).</summary>
    public async Task<Result<IReadOnlyList<NonWorkingDay>>> NonWorkingDaysAsync(SiteId siteId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var site = await sites.GetAsync(siteId, cancellationToken).ConfigureAwait(false);
        if (!site.IsSuccess)
        {
            return site.Error;
        }

        var start = from ?? ShiftCalendar.Today(site.Value.TimeZone, clock.GetUtcNow());
        var end = to ?? start.AddDays(ShiftCalendar.MaxComputedDays - 1);
        return IsRange(start, end)
            ? Result.Success<IReadOnlyList<NonWorkingDay>>([.. (await store.NonWorkingDaysAsync(siteId, start, end, cancellationToken).ConfigureAwait(false)).OrderBy(d => d.Date)])
            : RangeInvalid;
    }

    /// <summary>Marks a date from the site's today on as non-working (today too: a closure for an incident); no shift starts on it.</summary>
    public async Task<Result<NonWorkingDay>> SetNonWorkingDayAsync(SiteId siteId, DateOnly date, NonWorkingDayInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var site = await ManagedSiteAsync(siteId, cancellationToken).ConfigureAwait(false);
        if (!site.IsSuccess)
        {
            return site.Error;
        }

        var details = ShiftRules.ValidateDay(input.Kind, input.Name);
        if (!details.IsSuccess)
        {
            return details.Error;
        }

        var today = ShiftCalendar.Today(site.Value.TimeZone, clock.GetUtcNow());
        if (date < today)
        {
            return DateInPast(today);
        }

        var day = (await store.NonWorkingDaysAsync(siteId, date, date, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (day is null)
        {
            day = NonWorkingDay.Create(clock, site.Value.TenantId, siteId, date, details.Value.Kind, details.Value.Name, caller.Subject);
            store.Add(day);
        }
        else if (!day.Change(clock, details.Value.Kind, details.Value.Name, caller.Subject))
        {
            return day;
        }

        return await ReconcileAndSaveAsync(site.Value, cancellationToken).ConfigureAwait(false) ? day : RecordVersions.Conflict;
    }

    public async Task<Result<bool>> RemoveNonWorkingDayAsync(SiteId siteId, DateOnly date, CancellationToken cancellationToken)
    {
        var site = await ManagedSiteAsync(siteId, cancellationToken).ConfigureAwait(false);
        if (!site.IsSuccess)
        {
            return site.Error;
        }

        var day = (await store.NonWorkingDaysAsync(siteId, date, date, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (day is null)
        {
            return DayNotFound;
        }

        var today = ShiftCalendar.Today(site.Value.TimeZone, clock.GetUtcNow());
        if (date < today)
        {
            return DateInPast(today);
        }

        day.Remove(clock, caller.Subject);
        store.Remove(day);
        return await ReconcileAndSaveAsync(site.Value, cancellationToken).ConfigureAwait(false) ? true : RecordVersions.Conflict;
    }

    /// <summary>
    /// The shifts of any range of up to a year (by default the stored days from the site's today), computed from the
    /// rules without storing them (US-P1-ORG-02 AC3).
    /// </summary>
    public async Task<Result<IReadOnlyList<ComputedShift>>> ComputeAsync(SiteId siteId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var site = await sites.GetAsync(siteId, cancellationToken).ConfigureAwait(false);
        if (!site.IsSuccess)
        {
            return site.Error;
        }

        var start = from ?? ShiftCalendar.Today(site.Value.TimeZone, clock.GetUtcNow());
        var end = to ?? start.AddDays(ShiftCalendar.StoredDays - 1);
        return IsRange(start, end)
            ? Result.Success<IReadOnlyList<ComputedShift>>(await instances.ComputeAsync(site.Value, start, end, cancellationToken).ConfigureAwait(false))
            : RangeInvalid;
    }

    private static bool IsRange(DateOnly from, DateOnly to) => to >= from && to.DayNumber - from.DayNumber < ShiftCalendar.MaxComputedDays;

    private async Task<Result<Site>> ManagedSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        var site = await sites.GetAsync(siteId, cancellationToken).ConfigureAwait(false);
        if (!site.IsSuccess)
        {
            return site;
        }

        if (!caller.Current.ScopeOf(OrganizationPermissions.ShiftsManage).CoversSite(siteId.Value))
        {
            return NotManaged;
        }

        return site.Value.Status == SiteStatus.Active ? site : SiteArchived;
    }

    /// <summary>
    /// Two people setting the same date (or the generator and a person replacing the same instances) at once: one is
    /// saved, the other is a 409 to try again (hardening plan H-C1, finding C-6; before, a 500).
    /// </summary>
    private async Task<bool> ReconcileAndSaveAsync(Site site, CancellationToken cancellationToken)
    {
        await instances.ReconcileAsync(site, cancellationToken).ConfigureAwait(false);
        return await store.TrySaveAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed record ShiftRun(int Tenants, int Sites, int Added, int Changed, int Removed, int Failed);

/// <summary>
/// The daily shift generator (US-P1-ORG-02 AC3, ADR-0024): for every tenant and site, the stored instances of the next
/// <see cref="ShiftCalendar.StoredDays"/> days follow the rules (new days appear, a site's clock change or archive is
/// applied). Idempotent; a failing tenant is logged (event 7001) and the others still run; a run in which every
/// tenant succeeded is reported in <c>karry.organization.shifts.last_success</c> (alert <c>KarryShiftInstancesStale</c>).
/// </summary>
public sealed partial class GenerateShiftInstances(IServiceScopeFactory scopes, ShiftMetrics metrics, ILogger<GenerateShiftInstances> logger)
{
    public async Task<ShiftRun> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantId> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken).ConfigureAwait(false);
        }

        var siteCount = 0;
        var failed = 0;
        var changes = default(InstanceChanges);
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
                var siteStore = scope.ServiceProvider.GetRequiredService<ISiteStore>();
                var instances = scope.ServiceProvider.GetRequiredService<ShiftInstances>();
                var tenantChanges = default(InstanceChanges);
                var sites = await siteStore.ListAsync(includeArchived: true, cancellationToken).ConfigureAwait(false);
                foreach (var site in sites)
                {
                    tenantChanges += await instances.ReconcileAsync(site, cancellationToken).ConfigureAwait(false);
                }

                if (!await scope.ServiceProvider.GetRequiredService<IShiftStore>().TrySaveAsync(cancellationToken).ConfigureAwait(false))
                {
                    // A person changed this company's shifts meanwhile: their change reconciled its site; the rest is
                    // filled by the next run (idempotent).
                    failed++;
                    LogTenantConflict(logger, tenant.ToString());
                    continue;
                }

                siteCount += sites.Count;
                changes += tenantChanges;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogTenantFailed(logger, ex, tenant.ToString());
            }
        }

        metrics.RunCompleted(failed == 0);
        LogRun(logger, tenants.Count, siteCount, changes.Added, changes.Changed, changes.Removed, failed);
        return new ShiftRun(tenants.Count, siteCount, changes.Added, changes.Changed, changes.Removed, failed);
    }

    [LoggerMessage(EventId = 7001, Level = LogLevel.Error, Message = "Shift instances of tenant {Tenant} could not be generated")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, string tenant);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Warning, Message = "Shift instances of tenant {Tenant} changed meanwhile; the next run fills them")]
    private static partial void LogTenantConflict(ILogger logger, string tenant);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Information,
        Message = "Shift instances: {Tenants} tenants, {Sites} sites, {Added} added, {Changed} changed, {Removed} removed, {Failed} failed")]
    private static partial void LogRun(ILogger logger, int tenants, int sites, int added, int changed, int removed, int failed);
}

/// <summary>Shift generator telemetry: the time of the last run in which every tenant succeeded.</summary>
public sealed class ShiftMetrics
{
    public const string MeterName = "Karry.Organization";

    private long lastSuccessUnixSeconds;

    public ShiftMetrics(IMeterFactory meterFactory, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        Clock = clock;
        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge("karry.organization.shifts.last_success", () =>
            Interlocked.Read(ref lastSuccessUnixSeconds) is var at and > 0 ? [new Measurement<long>(at)] : Array.Empty<Measurement<long>>(),
            "s", "Unix time of the last shift generation in which every tenant succeeded.");
    }

    private TimeProvider Clock { get; }

    public void RunCompleted(bool allSucceeded)
    {
        if (allSucceeded)
        {
            Interlocked.Exchange(ref lastSuccessUnixSeconds, Clock.GetUtcNow().ToUnixTimeSeconds());
        }
    }
}
