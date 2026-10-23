using Karry.Modules.Tenancy.Domain;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Tenancy.Tests;

[Trait("Story", "US-P1-TEN-01")]
public sealed class TenantDomainTests
{
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("acme", "acme")]
    [InlineData("  Carriere-Sfax ", "carriere-sfax")]
    [InlineData("abc", "abc")]
    [InlineData("a1-b2-c3", "a1-b2-c3")]
    [InlineData("0123456789012345678901234567890123456789", "0123456789012345678901234567890123456789")]
    public void A_valid_slug_is_trimmed_and_lowercased(string input, string expected) =>
        TenantSlug.Create(input).Value.Value.Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("01234567890123456789012345678901234567890")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("ac--me")]
    [InlineData("xn--80ak6aa92e")]
    [InlineData("acme.sfax")]
    [InlineData("acme_sfax")]
    [InlineData("carrière")]
    [InlineData("acme sfax")]
    public void An_invalid_slug_is_refused(string? input) =>
        TenantSlug.Create(input).Error!.Code.Should().Be("tenant.slug_invalid");

    [Theory]
    [InlineData("www")]
    [InlineData("API")]
    [InlineData("admin")]
    [InlineData("demo")]
    [InlineData("karry")]
    public void A_reserved_slug_is_refused(string input) =>
        TenantSlug.Create(input).Error!.Code.Should().Be("tenant.slug_reserved");

    [Theory]
    [InlineData("  Carrière   de   Sfax  ", "Carrière de Sfax")]
    [InlineData("AB", "AB")]
    public void A_name_is_normalised(string input, string expected) =>
        Tenant.NormalizeName(input).Value.Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("A")]
    [InlineData("Bad\u0000Name")]
    public void An_invalid_name_is_refused(string? input) =>
        Tenant.NormalizeName(input).Error!.Code.Should().Be("tenant.name_invalid");

    [Fact]
    public void A_name_of_121_characters_is_refused_and_120_accepted()
    {
        Tenant.NormalizeName(new string('a', 121)).IsSuccess.Should().BeFalse();
        Tenant.NormalizeName(new string('a', 120)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Provisioning_creates_an_active_tenant_and_raises_one_event()
    {
        var tenant = Tenant.Provision(clock, "carriere-sfax", "Carrière de Sfax").Value;

        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.CreatedAt.Should().Be(clock.GetUtcNow());
        tenant.Id.Value.Version.Should().Be(7);
        var provisioned = tenant.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<TenantProvisioned>().Subject;
        provisioned.Should().BeEquivalentTo(new { TenantId = tenant.Id.Value, Slug = "carriere-sfax", Name = "Carrière de Sfax" });
    }

    [Fact]
    public void Provisioning_with_an_invalid_slug_creates_nothing() =>
        Tenant.Provision(clock, "x", "Some Company").IsSuccess.Should().BeFalse();
}
