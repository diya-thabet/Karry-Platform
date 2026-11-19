using Karry.BuildingBlocks.Authorization;
using Karry.Modules.Integrity.Domain;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Integrity.Application;

/// <summary>Integrity permissions (ADR-0036). Site-scoped: a site role reads the flags of its sites only.</summary>
public static class IntegrityPermissions
{
    /// <summary>Read the integrity flags of the caller's sites (P2.7; settling them is P2.8's controller console).</summary>
    public const string FlagsRead = "integrity.flags.read";

    /// <summary>
    /// Switch the company's non-security rules on or off and set their thresholds (ADR-0036 §1): it decides what gets
    /// flagged, so it is a company administrator's, with a second factor and a recent sign-in.
    /// </summary>
    public const string RulesManage = "integrity.rules.manage";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(FlagsRead, ScopeKind.Site, privileged: false,
            Roles.TenantAdmin, Roles.Executive, Roles.Controller, Roles.SiteManager, Roles.MaintenancePlanner),
        new(RulesManage, ScopeKind.Tenant, privileged: true, Roles.TenantAdmin),
    ];
}

/// <summary>A checked search of flags; <c>Sites</c> null means every site of the company (and flags without a site).</summary>
public sealed record FlagQuery(IReadOnlySet<Guid>? Sites, Guid? SiteId, string? Rule, bool? Open, string? SubjectType, Guid? SubjectId, Guid? After, int Take);

/// <summary>Reads flags for the portal (RLS: the current company only).</summary>
public interface IFlagReader
{
    /// <summary>Flags matching the query, newest first (UUIDv7 order).</summary>
    Task<IReadOnlyList<IntegrityFlag>> SearchAsync(FlagQuery query, CancellationToken cancellationToken);
}

/// <summary>A page of flags, newest first; <c>NextCursor</c> is the last flag ID of the page when more follow.</summary>
public sealed record FlagPage(IReadOnlyList<IntegrityFlag> Items, Guid? NextCursor);

/// <summary>The portal's reading of integrity flags (ADR-0036 §2), limited to the caller's sites (ADR-0022).</summary>
public sealed class FlagReading(IFlagReader reader, ICallerAccess caller)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static readonly Error LimitInvalid = Error.Validation("integrity.limit_invalid", $"A page holds 1 to {MaxLimit} flags.");

    public static readonly Error StatusInvalid = Error.Validation("integrity.status_invalid", "The status is open, closed or all.");

    public static readonly Error RuleInvalid = Error.Validation("integrity.rule_invalid", "No such rule.");

    public async Task<Result<FlagPage>> SearchAsync(
        Guid? siteId, string? rule, string? status, string? subjectType, Guid? subjectId, Guid? cursor, int? limit, CancellationToken cancellationToken)
    {
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            return LimitInvalid;
        }

        if (status is not (null or "open" or "closed" or "all"))
        {
            return StatusInvalid;
        }

        if (rule is not null && RuleCatalog.Find(rule) is null)
        {
            return RuleInvalid;
        }

        bool? open = status switch { null or "open" => true, "closed" => false, _ => null };
        var scope = caller.Current.ScopeOf(IntegrityPermissions.FlagsRead);
        var found = await reader.SearchAsync(
            new FlagQuery(scope.TenantWide ? null : scope.Sites, siteId, rule, open, subjectType, subjectId, cursor, take + 1), cancellationToken).ConfigureAwait(false);
        return found.Count > take ? new FlagPage([.. found.Take(take)], found[take - 1].Id.Value) : new FlagPage(found, null);
    }
}
