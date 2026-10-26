using Karry.Modules.Organization.Domain;

namespace Karry.Modules.Organization.Tests;

/// <summary>Site input rules (US-P1-ORG-01 AC1, ADR-0024): each refusal has its own code.</summary>
[Trait("Story", "US-P1-ORG-01")]
public sealed class SiteRulesTests
{
    // About 1 km × 1 km near Tunis, as GeoJSON [longitude, latitude].
    private static readonly double[][] Square =
    [
        [10.0000, 36.8000], [10.0112, 36.8000], [10.0112, 36.8090], [10.0000, 36.8090], [10.0000, 36.8000],
    ];

    private static IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>> Rings(params double[][] points) => [[.. points.Select(p => (IReadOnlyList<double>)p)]];

    private static Karry.SharedKernel.Results.Result<SiteDetails> Valid(
        string code = "car-nord", string type = "quarry", string country = "tn", double latitude = 36.8, double longitude = 10.0,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>>? boundary = null, string zone = "Africa/Tunis", string? currency = null, string name = "Carrière Nord") =>
        SiteRules.Validate(code, name, type, "Route de Bizerte km 12", "Tunis", null, "1000", country, latitude, longitude, boundary, zone, currency);

    [Fact]
    public void Valid_input_is_normalised()
    {
        var site = Valid(boundary: Rings(Square), currency: "eur").Value;

        site.Code.Should().Be("CAR-NORD");
        site.Type.Should().Be(SiteType.Quarry);
        site.Address.Country.Should().Be("TN");
        site.Location.X.Should().Be(10.0, "X is the longitude");
        site.Location.Y.Should().Be(36.8);
        site.Location.SRID.Should().Be(4326);
        site.Boundary!.Shell.NumPoints.Should().Be(5);
        site.Currency.Should().Be("EUR");
        SiteRules.AreaKm2(site.Boundary).Should().BeApproximately(1.0, 0.05, "about 1 km × 1 km");
    }

    [Theory]
    [InlineData("x", "quarry", "TN", 36.8, 10.0, "Africa/Tunis", "organization.site_code_invalid")]
    [InlineData("CAR NORD", "quarry", "TN", 36.8, 10.0, "Africa/Tunis", "organization.site_code_invalid")]
    [InlineData("CAR-", "quarry", "TN", 36.8, 10.0, "Africa/Tunis", "organization.site_code_invalid")]
    [InlineData("CAR-NORD", "factory", "TN", 36.8, 10.0, "Africa/Tunis", "organization.site_type_invalid")]
    [InlineData("CAR-NORD", "quarry", "XX", 36.8, 10.0, "Africa/Tunis", "organization.site_address_invalid")]
    [InlineData("CAR-NORD", "quarry", "TN", 91.0, 10.0, "Africa/Tunis", "organization.site_location_invalid")]
    [InlineData("CAR-NORD", "quarry", "TN", 36.8, -181.0, "Africa/Tunis", "organization.site_location_invalid")]
    [InlineData("CAR-NORD", "quarry", "TN", double.NaN, 10.0, "Africa/Tunis", "organization.site_location_invalid")]
    [InlineData("CAR-NORD", "quarry", "TN", 36.8, 10.0, "Tunis", "organization.time_zone_unknown")]
    public void Invalid_input_is_refused_with_its_own_code(string code, string type, string country, double latitude, double longitude, string zone, string error) =>
        Valid(code, type, country, latitude, longitude, zone: zone).Error!.Code.Should().Be(error);

    [Fact]
    public void Names_currencies_and_types_are_checked()
    {
        Valid(name: "x").Error!.Code.Should().Be("organization.site_name_invalid");
        Valid(currency: "EUO").Error!.Code.Should().Be("organization.currency_unsupported");
        SiteRules.TypeCodes.Should().Equal("quarry", "sand-washing", "mine", "warehouse-only", "workshop");
    }

    [Fact]
    public void A_boundary_must_be_one_closed_simple_ring_of_reasonable_size()
    {
        Valid(boundary: Rings(Square[..4])).Error!.Code.Should().Be("organization.boundary_not_closed");
        Valid(boundary: Rings(Square[..3])).Error!.Code.Should().Be("organization.boundary_invalid", "fewer than 4 points");
        Valid(boundary: [Rings(Square)[0], Rings(Square)[0]]).Error!.Code.Should().Be("organization.boundary_invalid", "holes are not accepted");
        Valid(boundary: Rings([10.0, 36.8], [10.01, 36.81], [10.0, 36.81], [10.01, 36.8], [10.0, 36.8]))
            .Error!.Code.Should().Be("organization.boundary_self_intersecting", "a bow tie crosses itself");
        Valid(boundary: Rings([10.0, 36.8], [10.0, 36.8], [10.0, 36.8], [10.0, 36.8]))
            .Error!.Code.Should().Be("organization.boundary_self_intersecting", "a ring without area");
        Valid(boundary: Rings([9.0, 36.0], [10.0, 36.0], [10.0, 37.0], [9.0, 37.0], [9.0, 36.0]))
            .Error!.Code.Should().Be("organization.boundary_too_large", "about 10 000 km²: a wrong coordinate");
        Valid(boundary: Rings([10.0, 36.8], [10.01, 36.8], [10.01, 91.0], [10.0, 36.8])).Error!.Code.Should().Be("organization.boundary_invalid");

        var tooMany = Enumerable.Range(0, SiteRules.MaxBoundaryPoints)
            .Select(i => new[] { 10.0 + (0.01 * Math.Cos(i * 2 * Math.PI / SiteRules.MaxBoundaryPoints)), 36.8 + (0.01 * Math.Sin(i * 2 * Math.PI / SiteRules.MaxBoundaryPoints)) })
            .ToList();
        tooMany.Add(tooMany[0]);
        Valid(boundary: Rings([.. tooMany])).Error!.Code.Should().Be("organization.boundary_invalid", "501 points");
        Valid(boundary: Rings([.. tooMany.Skip(1)])).IsSuccess.Should().BeFalse("not closed any more");
    }

    [Fact]
    public void Changing_a_site_keeps_its_code_and_records_before_and_after()
    {
        var site = Site.Create(TimeProvider.System, new SharedKernel.Tenancy.TenantId(Guid.NewGuid()), Valid().Value, "admin");
        site.ClearDomainEvents();

        site.Change(TimeProvider.System, Valid().Value, "admin").Value.Should().BeFalse("nothing changed");
        site.Change(TimeProvider.System, Valid(code: "CAR-SUD").Value, "admin").Error!.Code.Should().Be("organization.site_code_fixed");
        site.Change(TimeProvider.System, Valid(name: "Carrière du Nord").Value, "admin").Value.Should().BeTrue();

        site.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<SiteChanged>()
            .Which.Should().Match<SiteChanged>(e => e.Before.Name == "Carrière Nord" && e.After.Name == "Carrière du Nord" && e.Before.Latitude == 36.8);
    }

    [Fact]
    public void Archive_and_restore_are_each_recorded_once()
    {
        var site = Site.Create(TimeProvider.System, new SharedKernel.Tenancy.TenantId(Guid.NewGuid()), Valid().Value, "admin");

        site.Archive(TimeProvider.System, "admin").Should().BeTrue();
        site.Archive(TimeProvider.System, "admin").Should().BeFalse();
        site.ArchivedAt.Should().NotBeNull();
        site.Restore(TimeProvider.System, "admin").Should().BeTrue();
        site.Restore(TimeProvider.System, "admin").Should().BeFalse();
        site.ArchivedAt.Should().BeNull();
        site.DomainEvents.Select(e => e.GetType().Name).Should().Equal("SiteCreated", "SiteArchived", "SiteRestored");
    }
}
