namespace Karry.Modules.Organization.Contracts;

/// <summary>A site as other modules see it.</summary>
public sealed record SiteSummary(Guid Id, string Code, string Name, string TimeZone, bool Archived);

/// <summary>The current tenant's sites, for other modules (e.g. site choice in role assignments). Read in the caller's unit of work (RLS).</summary>
public interface ISiteDirectory
{
    Task<SiteSummary?> FindAsync(Guid siteId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SiteSummary>> ListAsync(CancellationToken cancellationToken);
}
