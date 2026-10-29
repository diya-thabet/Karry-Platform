using System.Text.Json;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Identity.Tests.Domain;

/// <summary>Support codes and support sessions of ADR-0029 §4, without the API.</summary>
[Trait("Story", "US-P1-OPS-03")]
public sealed class SupportTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());

    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 29, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_support_code_is_an_enrollment_style_code_valid_thirty_minutes_and_only_its_hash_is_kept()
    {
        var (consent, code) = SupportConsent.Create(clock, Tenant, "admin");

        code.Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){3}$");
        consent.CodeHash.Should().Be(EnrollmentCode.Hash(EnrollmentCode.Normalize(code)!));
        consent.ExpiresAt.Should().Be(clock.GetUtcNow().AddMinutes(30));
        var created = consent.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<SupportConsentCreated>().Subject;
        created.Should().BeEquivalentTo(new { ConsentId = consent.Id.Value, consent.ExpiresAt, Actor = "admin" });
        JsonSerializer.Serialize(created).Should().NotContain(code[..4]);
    }

    [Fact]
    public void A_code_works_once_and_not_after_thirty_minutes()
    {
        var (consent, _) = SupportConsent.Create(clock, Tenant, "admin");
        clock.Advance(TimeSpan.FromMinutes(29));
        consent.Use(clock).IsSuccess.Should().BeTrue();
        consent.UsedAt.Should().Be(clock.GetUtcNow());
        consent.Use(clock).Error.Should().Be(SupportConsent.Invalid);

        var (late, _) = SupportConsent.Create(clock, Tenant, "admin");
        clock.Advance(TimeSpan.FromMinutes(30));
        late.Use(clock).Error.Should().Be(SupportConsent.Invalid);
        late.UsedAt.Should().BeNull();
    }

    [Fact]
    public void A_session_with_a_code_lasts_one_hour_and_is_ledgered_with_its_operator()
    {
        var (consent, _) = SupportConsent.Create(clock, Tenant, "admin");

        var session = SupportSession.WithConsent(clock, Tenant, consent, "op-1", "  Ops Karry ");

        session.Should().BeEquivalentTo(new
        {
            TenantId = Tenant,
            Mode = SupportMode.Consent,
            OperatorSubject = "op-1",
            OperatorName = "Ops Karry",
            Reason = (string?)null,
            ConsentId = (SupportConsentId?)consent.Id,
            StartedAt = clock.GetUtcNow(),
            EndsAt = clock.GetUtcNow().AddHours(1),
        });
        session.DomainEvents.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            SessionId = session.Id.Value,
            Mode = "consent",
            Operator = "op-1",
            OperatorName = "Ops Karry",
            Reason = (string?)null,
            ConsentId = (Guid?)consent.Id.Value,
            session.EndsAt,
            OccurredAt = session.StartedAt,
        });
        session.IsActive(clock.GetUtcNow().AddMinutes(59)).Should().BeTrue();
        session.IsActive(clock.GetUtcNow().AddHours(1)).Should().BeFalse();
        session.Until.Should().Be(session.EndsAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Customer unreachable")]
    [InlineData("   Production incident at Sfax 1   ")] // 29 characters once trimmed
    public void Break_glass_needs_a_written_reason_of_thirty_characters(string? reason) =>
        SupportSession.BreakGlass(clock, Tenant, "op-1", null, reason).Error.Should().Be(SupportSession.ReasonTooShort);

    [Fact]
    public void Break_glass_keeps_its_reason_trimmed_and_at_most_five_hundred_characters()
    {
        SupportSession.BreakGlass(clock, Tenant, "op-1", null, " Production incident at Sfax 12 ").Value.Reason.Should().HaveLength(30);
        var session = SupportSession.BreakGlass(clock, Tenant, "op-1", "Ops", "  Incident INC-42: weighbridge totals wrong at the Sfax site  ").Value;
        session.Should().BeEquivalentTo(new { Mode = SupportMode.BreakGlass, Reason = "Incident INC-42: weighbridge totals wrong at the Sfax site", ConsentId = (SupportConsentId?)null });
        session.DomainEvents.OfType<SupportSessionStarted>().Single().Should().BeEquivalentTo(new { Mode = "break_glass", session.Reason, ConsentId = (Guid?)null });

        SupportSession.BreakGlass(clock, Tenant, "op-1", "Ops", new string('y', 600)).Value.Reason.Should().HaveLength(SupportSession.ReasonMaxLength);
        SupportSession.BreakGlass(clock, Tenant, "op-1", new string('n', 300), new string('y', 40)).Value.OperatorName.Should().HaveLength(SupportSession.OperatorNameMaxLength);
        SupportSession.BreakGlass(clock, Tenant, "op-1", " ", new string('y', 40)).Value.OperatorName.Should().BeNull();
    }

    [Fact]
    public void A_session_ends_once_early_and_not_after_its_hour()
    {
        var session = SupportSession.BreakGlass(clock, Tenant, "op-1", "Ops", new string('r', 40)).Value;
        clock.Advance(TimeSpan.FromMinutes(20));

        session.End(clock, "admin", byOperator: false).IsSuccess.Should().BeTrue();
        session.Should().BeEquivalentTo(new { EndedAt = clock.GetUtcNow(), EndedBy = "admin", Until = clock.GetUtcNow() });
        session.IsActive(clock.GetUtcNow()).Should().BeFalse();
        session.DomainEvents.OfType<SupportSessionEnded>().Single().Should().BeEquivalentTo(new { SessionId = session.Id.Value, Actor = "admin", ByOperator = false });
        session.End(clock, "admin", byOperator: false).Error.Should().Be(SupportSession.AlreadyEnded);

        var other = SupportSession.BreakGlass(clock, Tenant, "op-1", "Ops", new string('r', 40)).Value;
        clock.Advance(TimeSpan.FromHours(1));
        other.End(clock, "op-1", byOperator: true).Error.Should().Be(SupportSession.AlreadyEnded);
        other.EndedAt.Should().BeNull();
    }
}
