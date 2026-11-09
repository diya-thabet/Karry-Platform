namespace Karry.Modules.Organization.Contracts;

/// <summary>A site's shift instance as other modules see it: its local date, code and real start and end.</summary>
public sealed record ResolvedShift(DateOnly Date, string Code, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string TimeZone);

/// <summary>
/// Which shift of a site an instant belongs to (ADR-0034 §2), computed from the site's rules (pattern versions,
/// non-working days, time zone), so a late entry resolves like one on time. Read in the caller's unit of work (RLS).
/// </summary>
public interface IShiftCalendar
{
    /// <summary>The shift running at <paramref name="instant"/>, or starting within an hour after it; null when none (or no such site).</summary>
    Task<ResolvedShift?> ResolveAsync(Guid siteId, DateTimeOffset instant, CancellationToken cancellationToken);
}
