using System.Text.RegularExpressions;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using NetTopologySuite.Geometries;
using NodaTime;
using SupportedCurrency = Karry.SharedKernel.Monetary.Currency;

namespace Karry.Modules.Organization.Domain;

public readonly record struct SiteId(Guid Value) : IEntityId<SiteId>
{
    public static SiteId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>What a site is (US-P1-ORG-01 AC1).</summary>
public enum SiteType
{
    Quarry = 1,
    SandWashing = 2,
    Mine = 3,
    WarehouseOnly = 4,
    Workshop = 5,
}

public enum SiteStatus
{
    Active = 1,

    /// <summary>Out of use: kept with its history, out of the default lists and the usage meter; can be restored.</summary>
    Archived = 2,
}

/// <summary>A site's postal address; <c>Country</c> is ISO 3166-1 alpha-2.</summary>
public sealed record SiteAddress(string Line, string City, string? Region, string? PostalCode, string Country);

/// <summary>The site's details as stored in the ledger: plain values, the boundary as WKT.</summary>
public sealed record SiteSnapshot(
    string Code, string Name, string Type, SiteAddress Address, double Latitude, double Longitude, string? Boundary, string TimeZone, string? Currency);

[EventType("organization.site_created.v1")]
public sealed record SiteCreated(Guid SiteId, SiteSnapshot Site, string? Actor) : DomainEvent;

[EventType("organization.site_changed.v1")]
public sealed record SiteChanged(Guid SiteId, SiteSnapshot Before, SiteSnapshot After, string? Actor) : DomainEvent;

[EventType("organization.site_archived.v1")]
public sealed record SiteArchived(Guid SiteId, string Code, string? Actor) : DomainEvent;

[EventType("organization.site_restored.v1")]
public sealed record SiteRestored(Guid SiteId, string Code, string? Actor) : DomainEvent;

/// <summary>Validated site details: what <see cref="Site.Create"/> and <see cref="Site.Change"/> take.</summary>
public sealed record SiteDetails(
    string Code, string Name, SiteType Type, SiteAddress Address, Point Location, Polygon? Boundary, string TimeZone, string? Currency);

/// <summary>
/// A place where the company works (US-P1-ORG-01): quarry, sand washing, mine, warehouse or workshop. Tenant-owned
/// (RLS) and scoped by its own ID: a site role sees and manages only its sites (ADR-0022). Archived, never deleted.
/// </summary>
public sealed class Site : AggregateRoot<SiteId>, ITenantOwned, ISiteScoped
{
    private Site(SiteId id, TenantId tenantId, SiteDetails details, DateTimeOffset at)
        : base(id)
    {
        TenantId = tenantId;
        Apply(details);
        Status = SiteStatus.Active;
        CreatedAt = at;
        ChangedAt = at;
    }

    // EF Core
    private Site()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    Guid? ISiteScoped.SiteId => Id.Value;

    /// <summary>Short code, unique in the company for ever (archived sites keep theirs), e.g. "CAR-NORD".</summary>
    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public SiteType Type { get; private set; }

    public string AddressLine { get; private set; } = null!;

    public string City { get; private set; } = null!;

    public string? Region { get; private set; }

    public string? PostalCode { get; private set; }

    public string Country { get; private set; } = null!;

    /// <summary>The map pin, WGS 84 (SRID 4326): X is the longitude, Y the latitude.</summary>
    public Point Location { get; private set; } = null!;

    /// <summary>Optional outline, WGS 84 (SRID 4326).</summary>
    public Polygon? Boundary { get; private set; }

    /// <summary>IANA time zone of the site's clock (shifts, daily figures).</summary>
    public string TimeZone { get; private set; } = null!;

    /// <summary>Currency of the site when it differs from the company's.</summary>
    public string? Currency { get; private set; }

    public SiteStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public SiteAddress Address => new(AddressLine, City, Region, PostalCode, Country);

    public SiteDetails Details => new(Code, Name, Type, Address, Location, Boundary, TimeZone, Currency);

    public static Site Create(TimeProvider clock, TenantId tenant, SiteDetails details, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(details);
        var at = clock.GetUtcNow();
        var site = new Site(EntityId.New<SiteId>(clock), tenant, details, at);
        site.Raise(new SiteCreated(site.Id.Value, Snapshot(details), actor) { OccurredAt = at });
        return site;
    }

    /// <summary>New details; false when nothing changed (no ledger entry). The code never changes.</summary>
    public Result<bool> Change(TimeProvider clock, SiteDetails details, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(details);
        if (!string.Equals(details.Code, Code, StringComparison.Ordinal))
        {
            return Error.Validation("organization.site_code_fixed", "A site keeps its code; create a new site for a new code.");
        }

        var before = Snapshot(Details);
        var after = Snapshot(details);
        if (before == after)
        {
            return false;
        }

        Apply(details);
        ChangedAt = clock.GetUtcNow();
        Raise(new SiteChanged(Id.Value, before, after, actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>Takes the site out of use; false when it already is.</summary>
    public bool Archive(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == SiteStatus.Archived)
        {
            return false;
        }

        Status = SiteStatus.Archived;
        ArchivedAt = ChangedAt = clock.GetUtcNow();
        Raise(new SiteArchived(Id.Value, Code, actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>Puts an archived site back in use (the caller checks the site limit first); false when it is active.</summary>
    public bool Restore(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == SiteStatus.Active)
        {
            return false;
        }

        Status = SiteStatus.Active;
        ArchivedAt = null;
        ChangedAt = clock.GetUtcNow();
        Raise(new SiteRestored(Id.Value, Code, actor) { OccurredAt = ChangedAt });
        return true;
    }

    private static SiteSnapshot Snapshot(SiteDetails details) =>
        new(details.Code, details.Name, SiteRules.TypeCode(details.Type), details.Address, details.Location.Y, details.Location.X,
            details.Boundary?.AsText(), details.TimeZone, details.Currency);

    private void Apply(SiteDetails details)
    {
        Code = details.Code;
        Name = details.Name;
        Type = details.Type;
        AddressLine = details.Address.Line;
        City = details.Address.City;
        Region = details.Address.Region;
        PostalCode = details.Address.PostalCode;
        Country = details.Address.Country;
        Location = details.Location;
        Boundary = details.Boundary;
        TimeZone = details.TimeZone;
        Currency = details.Currency;
    }
}

/// <summary>Validation of site input (US-P1-ORG-01 AC1, ADR-0024); every refusal has its own code.</summary>
public static partial class SiteRules
{
    public const int CodeMaxLength = 20;
    public const int NameMaxLength = 120;
    public const int AddressMaxLength = 200;
    public const int MaxBoundaryPoints = 500;

    /// <summary>Largest accepted boundary (km²): far beyond any quarry, small enough to catch a wrong coordinate.</summary>
    public const double MaxBoundaryAreaKm2 = 200;

    public const int Srid = 4326;

    private static readonly GeometryFactory Geometries = new(new PrecisionModel(), Srid);

    private static readonly Dictionary<string, SiteType> Types = new(StringComparer.Ordinal)
    {
        ["quarry"] = SiteType.Quarry,
        ["sand-washing"] = SiteType.SandWashing,
        ["mine"] = SiteType.Mine,
        ["warehouse-only"] = SiteType.WarehouseOnly,
        ["workshop"] = SiteType.Workshop,
    };

    public static IEnumerable<string> TypeCodes => Types.Keys;

    public static string TypeCode(SiteType type) => Types.Single(t => t.Value == type).Key;

    /// <summary>Checks raw input and builds the details; <paramref name="boundary"/> is a GeoJSON polygon's rings ([longitude, latitude]).</summary>
    public static Result<SiteDetails> Validate(
        string? code, string? name, string? type, string? addressLine, string? city, string? region, string? postalCode, string? country,
        double latitude, double longitude, IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>>? boundary, string? timeZone, string? currency)
    {
        var normalizedCode = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(normalizedCode))
        {
            return Error.Validation("organization.site_code_invalid", $"A site code is 2–{CodeMaxLength} letters, digits or dashes, e.g. CAR-NORD.");
        }

        if (Text(name, NameMaxLength) is not { Length: >= 2 } validName)
        {
            return Error.Validation("organization.site_name_invalid", $"A site name is 2–{NameMaxLength} characters.");
        }

        if (type is null || !Types.TryGetValue(type, out var siteType))
        {
            return Error.Validation("organization.site_type_invalid", $"The site type must be one of: {string.Join(", ", Types.Keys)}.");
        }

        var line = Text(addressLine, AddressMaxLength);
        var town = Text(city, NameMaxLength);
        var countryCode = (country ?? string.Empty).Trim().ToUpperInvariant();
        if (line is not { Length: > 0 } || town is not { Length: > 0 } || !Countries.IsKnown(countryCode)
            || Text(region, NameMaxLength) is null && region is not null || Text(postalCode, 20) is null && postalCode is not null)
        {
            return Error.Validation("organization.site_address_invalid", "The address needs a street line, a city and an ISO country code (e.g. TN).");
        }

        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            return Error.Validation("organization.site_location_invalid", "The location needs a latitude (−90 to 90) and a longitude (−180 to 180).");
        }

        Polygon? outline = null;
        if (boundary is not null)
        {
            var polygon = Polygon(boundary);
            if (!polygon.IsSuccess)
            {
                return polygon.Error;
            }

            outline = polygon.Value;
        }

        if (timeZone is null || DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone) is null)
        {
            return Error.Validation("organization.time_zone_unknown", "The time zone must be an IANA name such as Africa/Tunis.");
        }

        var currencyCode = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant();
        if (currencyCode is not null && !SupportedCurrency.SupportedCodes.Contains(currencyCode))
        {
            return Error.Validation("organization.currency_unsupported", "The currency is not supported.");
        }

        var address = new SiteAddress(line, town, Empty(region), Empty(postalCode), countryCode);
        var location = Geometries.CreatePoint(new Coordinate(longitude, latitude));
        return new SiteDetails(normalizedCode, validName, siteType, address, location, outline, timeZone, currencyCode);
    }

    /// <summary>
    /// A boundary is one closed ring (first point = last point) of 4–500 points, valid and simple (no self-intersection),
    /// with coordinates in range and an area of at most <see cref="MaxBoundaryAreaKm2"/>. Holes are not accepted.
    /// </summary>
    public static Result<Polygon> Polygon(IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>> rings)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (rings.Count != 1 || rings[0] is not { Count: >= 4 and <= MaxBoundaryPoints } ring
            || ring.Any(p => p is not { Count: 2 } || !double.IsFinite(p[0]) || !double.IsFinite(p[1]) || p[0] is < -180 or > 180 || p[1] is < -90 or > 90))
        {
            return Error.Validation("organization.boundary_invalid", $"A boundary is one ring of 4–{MaxBoundaryPoints} [longitude, latitude] points.");
        }

        var coordinates = ring.Select(p => new Coordinate(p[0], p[1])).ToArray();
        if (!coordinates[0].Equals2D(coordinates[^1]))
        {
            return Error.Validation("organization.boundary_not_closed", "A boundary must end where it starts.");
        }

        var polygon = Geometries.CreatePolygon(coordinates);
        if (!polygon.IsValid || !polygon.Shell.IsSimple || polygon.Area == 0)
        {
            return Error.Validation("organization.boundary_self_intersecting", "A boundary must not cross itself.");
        }

        return AreaKm2(polygon) > MaxBoundaryAreaKm2
            ? Error.Validation("organization.boundary_too_large", $"A boundary covers at most {MaxBoundaryAreaKm2} km²; check the coordinates.")
            : polygon;
    }

    /// <summary>Approximate area on the sphere (equirectangular projection at the polygon's latitude): enough for a sanity limit.</summary>
    public static double AreaKm2(Polygon polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        const double KmPerDegree = 111.32;
        var latitude = polygon.Centroid.Y * Math.PI / 180;
        return polygon.Area * KmPerDegree * KmPerDegree * Math.Cos(latitude);
    }

    private static string? Text(string? value, int max)
    {
        var text = string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > max || text.Any(char.IsControl) ? null : text;
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : Text(value, AddressMaxLength);

    [GeneratedRegex("^[A-Z0-9](?:[A-Z0-9-]{0,18}[A-Z0-9])$")]
    private static partial Regex CodePattern();
}
