using Karry.BuildingBlocks.Entitlements;
using Karry.Modules.Organization.Application;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Organization.Domain;
using Karry.SharedKernel.Entitlements;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.Organization.Infrastructure;

internal sealed class SiteStore(OrganizationDbContext db) : ISiteStore
{
    public Task<Site?> FindAsync(SiteId id, CancellationToken cancellationToken) =>
        db.Sites.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Site>> ListAsync(bool includeArchived, CancellationToken cancellationToken) =>
        await db.Sites.AsNoTracking().Where(s => includeArchived || s.Status == SiteStatus.Active).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<bool> TrySaveAsync(Site site, CancellationToken cancellationToken)
    {
        if (db.Entry(site).State == EntityState.Detached)
        {
            db.Sites.Add(site);
        }

        try
        {
            await db.SaveChangesSerializedAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<IReadOnlyList<Site>> FindByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        await db.Sites.Where(x => codes.Contains(x.Code)).ToListAsync(cancellationToken).ConfigureAwait(false);

    public void Add(Site site) => db.Sites.Add(site);

    public async Task<bool> TrySaveAllAsync(CancellationToken cancellationToken)
    {
        // One SaveChanges is one transaction: the sites, their outbox rows and ledger entries.
        try
        {
            await db.SaveChangesSerializedAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

/// <summary>The site meter (US-P1-ORG-01 AC2): active sites; an archived site frees its place.</summary>
internal sealed class ActiveSites(OrganizationDbContext db) : IUsageSource
{
    public string Metric => Metrics.Sites;

    public async Task<long> CountAsync(CancellationToken cancellationToken) =>
        await db.Sites.LongCountAsync(s => s.Status == SiteStatus.Active, cancellationToken).ConfigureAwait(false);
}

/// <summary>RULE-FO-04's distance, computed by PostGIS on the geography (metres on the ellipsoid, 0 inside the polygon).</summary>
internal sealed class SiteGeofence(OrganizationDbContext db) : ISiteGeofence
{
    public async Task<decimal?> MetresOutsideAsync(Guid siteId, decimal latitude, decimal longitude, CancellationToken cancellationToken)
    {
        var (lat, lon) = ((double)latitude, (double)longitude);
        var metres = await db.Database.SqlQuery<double?>($"""
            SELECT ST_Distance(boundary::geography, ST_SetSRID(ST_MakePoint({lon}, {lat}), 4326)::geography) AS "Value"
            FROM organization.sites WHERE id = {siteId} AND boundary IS NOT NULL
            """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return metres is { } m ? Math.Round((decimal)m, 0, MidpointRounding.AwayFromZero) : null;
    }
}

internal sealed class SiteDirectory(OrganizationDbContext db) : ISiteDirectory
{
    public Task<SiteSummary?> FindAsync(Guid siteId, CancellationToken cancellationToken) =>
        db.Sites.AsNoTracking().Where(s => s.Id == new SiteId(siteId))
            .Select(s => new SiteSummary(s.Id.Value, s.Code, s.Name, s.TimeZone, s.Status == SiteStatus.Archived))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SiteSummary>> ListAsync(CancellationToken cancellationToken) =>
        await db.Sites.AsNoTracking().OrderBy(s => s.Code)
            .Select(s => new SiteSummary(s.Id.Value, s.Code, s.Name, s.TimeZone, s.Status == SiteStatus.Archived))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}
