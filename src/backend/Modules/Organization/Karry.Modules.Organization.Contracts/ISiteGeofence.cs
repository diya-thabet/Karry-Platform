namespace Karry.Modules.Organization.Contracts;

/// <summary>
/// How far a position is from a site's drawn boundary, for the geofence rule (RULE-FO-04, ADR-0036). Read in the
/// caller's unit of work (RLS).
/// </summary>
public interface ISiteGeofence
{
    /// <summary>
    /// The distance in whole metres from the position (WGS 84 degrees) to the site's boundary: 0 inside it; null when
    /// the site has no boundary (or is not in this company), so nothing can be judged.
    /// </summary>
    Task<decimal?> MetresOutsideAsync(Guid siteId, decimal latitude, decimal longitude, CancellationToken cancellationToken);
}
