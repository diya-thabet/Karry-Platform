using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Identity.Tests.Domain;

/// <summary>Enrollment codes, tablets and the PIN checks of ADR-0028, without the API.</summary>
[Trait("Story", "US-P1-OFF-02")]
public sealed class DeviceTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());
    private static readonly Guid Site = Guid.CreateVersion7();

    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_code_is_sixteen_crockford_characters_in_four_groups_and_only_its_hash_is_kept()
    {
        var (enrollment, code) = DeviceEnrollment.Create(clock, Tenant, Site, "admin");

        code.Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){3}$");
        enrollment.CodeHash.Should().Be(EnrollmentCode.Hash(code.Replace("-", string.Empty, StringComparison.Ordinal))).And.NotContain(code);
        enrollment.ExpiresAt.Should().Be(clock.GetUtcNow().AddMinutes(15));
        Enumerable.Range(0, 200).Select(_ => EnrollmentCode.New()).Distinct().Should().HaveCount(200);
        JsonSerializer.Serialize(enrollment.DomainEvents.Single(), enrollment.DomainEvents.Single().GetType()).Should().NotContain(code[..4]);
    }

    [Theory]
    [InlineData("abcd-efgh-jkmn-pqrs", "ABCDEFGHJKMNPQRS")]
    [InlineData(" ABCD EFGH JKMN PQRS ", "ABCDEFGHJKMNPQRS")]
    [InlineData("o0il-0000-0000-0000", "0011000000000000")]
    public void Typing_is_forgiving(string typed, string normalized) =>
        EnrollmentCode.Normalize(typed).Should().Be(normalized);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCD-EFGH-JKMN-PQR")]
    [InlineData("ABCD-EFGH-JKMN-PQRST")]
    [InlineData("ABCD-EFGH-JKMN-PQRU")]
    [InlineData("ABCD-EFGH-JKMN-PQR!")]
    public void What_cannot_be_a_code_is_refused_before_any_lookup(string? typed) =>
        EnrollmentCode.Normalize(typed).Should().BeNull();

    [Fact]
    public void A_tablet_enrols_once_with_a_proof_of_its_key_within_fifteen_minutes()
    {
        var (enrollment, code) = DeviceEnrollment.Create(clock, Tenant, Site, "admin");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        clock.Advance(TimeSpan.FromMinutes(14));

        var device = Enrol(enrollment, code, key).Value;

        device.Should().BeEquivalentTo(new { TenantId = Tenant, SiteId = Site, Name = "Tablette pesage", Status = DeviceStatus.Active, EnrolledAt = clock.GetUtcNow() });
        device.PinKey.Should().HaveCount(32);
        enrollment.UsedAt.Should().Be(clock.GetUtcNow());
        enrollment.DeviceId.Should().Be(device.Id);
        device.DomainEvents.Single().Should().BeOfType<DeviceEnrolled>().Which.EnrollmentId.Should().Be(enrollment.Id.Value);

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Enrol(enrollment, code, other).Error.Should().Be(DeviceEnrollment.Expired, "single use");
    }

    [Fact]
    public void A_code_expires_after_fifteen_minutes()
    {
        var (enrollment, code) = DeviceEnrollment.Create(clock, Tenant, Site, "admin");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        clock.Advance(TimeSpan.FromMinutes(15));

        Enrol(enrollment, code, key).Error.Should().Be(DeviceEnrollment.Expired);
    }

    [Fact]
    public void A_wrong_proof_or_a_bad_name_does_not_use_the_code()
    {
        var (enrollment, code) = DeviceEnrollment.Create(clock, Tenant, Site, "admin");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var thief = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var point = key.ExportParameters(false).Q;

        var stolen = Device.Enrol(clock, enrollment, code, point.X!, point.Y!, Proof(thief, code), "Tablette", null, null, null);
        var otherCode = Device.Enrol(clock, enrollment, code, point.X!, point.Y!, Proof(key, "AAAA-AAAA-AAAA-AAAA"), "Tablette", null, null, null);
        var notAPoint = Device.Enrol(clock, enrollment, code, new byte[32], new byte[32], Proof(key, code), "Tablette", null, null, null);
        var noName = Device.Enrol(clock, enrollment, code, point.X!, point.Y!, Proof(key, code), "  ", null, null, null);

        new[] { stolen, otherCode, notAPoint }.Select(r => r.Error!.Code).Should().AllBe("devices.key_invalid");
        noName.Error!.Code.Should().Be("devices.name_invalid");
        enrollment.UsedAt.Should().BeNull();
        Enrol(enrollment, code, key).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_revoked_tablet_stays_revoked_and_last_seen_is_written_once_a_minute()
    {
        var (enrollment, code) = DeviceEnrollment.Create(clock, Tenant, Site, "admin");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var device = Enrol(enrollment, code, key).Value;

        clock.Advance(TimeSpan.FromSeconds(59));
        device.Seen(clock).Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        device.Seen(clock).Should().BeTrue();
        device.LastSeenAt.Should().Be(clock.GetUtcNow());

        device.Revoke(clock, "admin").IsSuccess.Should().BeTrue();
        device.Should().BeEquivalentTo(new { Status = DeviceStatus.Revoked, RevokedAt = clock.GetUtcNow(), RevokedBy = "admin" });
        device.DomainEvents.OfType<DeviceRevoked>().Should().ContainSingle();
        device.Revoke(clock, "admin").Error!.Code.Should().Be("devices.already_revoked");
    }

    [Fact]
    public void Pin_verifiers_match_the_published_vectors_the_tablet_is_tested_with()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(PostgresServer.RepositoryRoot(), "tests", "vectors", "pin-verifier.json")));
        foreach (var vector in file.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var stored = vector.GetProperty("stored").GetString();
            var verifier = Pin.Verifier(stored, Convert.FromBase64String(vector.GetProperty("pinKey").GetString()!));

            verifier.Should().Be(new PinVerifier(vector.GetProperty("salt").GetString()!, vector.GetProperty("iterations").GetInt32(), vector.GetProperty("verifier").GetString()!));
        }

        Pin.Verifier("pbkdf2-sha256$x$AA==$AA==", new byte[32]).Should().BeNull();
        Pin.Verifier("bcrypt$1000$AA==$AA==", new byte[32]).Should().BeNull();
        Pin.Verifier("pbkdf2-sha256$1000$AA==$not base64", new byte[32]).Should().BeNull();
    }

    [Fact]
    [Trait("Story", "US-P1-OFF-04")]
    public void Five_wrong_pins_lock_the_user_for_fifteen_minutes_with_a_ledger_event()
    {
        var user = FieldUser();
        var device = Guid.CreateVersion7();

        user.UnlockWithPin(clock, "804613", device).IsSuccess.Should().BeTrue();
        for (var i = 0; i < 4; i++)
        {
            user.UnlockWithPin(clock, "111112", device).Error!.Code.Should().Be("identity.pin_wrong");
        }

        user.UnlockWithPin(clock, "111112", device).Error!.Code.Should().Be("identity.pin_locked");
        user.DomainEvents.OfType<UserPinLocked>().Single().Should().BeEquivalentTo(new { UserId = user.Id.Value, DeviceId = device, LockedUntil = clock.GetUtcNow().AddMinutes(15), OccurredAt = clock.GetUtcNow() });
        user.UnlockWithPin(clock, "804613", device).Error!.Code.Should().Be("identity.pin_locked", "even the right PIN waits");

        clock.Advance(TimeSpan.FromMinutes(15));
        user.UnlockWithPin(clock, "804613", device).IsSuccess.Should().BeTrue();
        user.UnlockWithPin(clock, "111112", device).Error!.Code.Should().Be("identity.pin_wrong", "the count started again");
    }

    [Fact]
    [Trait("Story", "US-P1-OFF-04")]
    public void A_right_pin_resets_the_count_and_a_new_pin_clears_a_lock()
    {
        var user = FieldUser();
        var device = Guid.CreateVersion7();
        for (var i = 0; i < 4; i++)
        {
            user.UnlockWithPin(clock, "111112", device);
        }

        user.UnlockWithPin(clock, "804613", device).IsSuccess.Should().BeTrue();
        user.UnlockWithPin(clock, "111112", device).Error!.Code.Should().Be("identity.pin_wrong");

        for (var i = 0; i < 4; i++)
        {
            user.UnlockWithPin(clock, "111112", device);
        }

        user.PinLockedUntil.Should().NotBeNull();
        user.SetPin(clock, "502719", "admin").IsSuccess.Should().BeTrue();
        user.UnlockWithPin(clock, "502719", device).IsSuccess.Should().BeTrue();
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-03")]
    [Trait("TestCase", "TC-P1-IAM-004")]
    public void A_deactivated_user_cannot_unlock()
    {
        var user = FieldUser();
        user.Deactivate(clock, "admin");

        user.UnlockWithPin(clock, "804613", Guid.CreateVersion7()).Error!.Code.Should().Be("identity.unlock_refused");
    }

    private Karry.SharedKernel.Results.Result<Device> Enrol(DeviceEnrollment enrollment, string code, ECDsa key)
    {
        var point = key.ExportParameters(false).Q;
        return Device.Enrol(clock, enrollment, code, point.X!, point.Y!, Proof(key, code), " Tablette pesage ", "Galaxy Tab A9", "Android 14", "1.0.0");
    }

    private static byte[] Proof(ECDsa key, string code) =>
        key.SignData(Encoding.UTF8.GetBytes($"karry-enroll:{code}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private UserAccount FieldUser()
    {
        var user = UserAccount.CreateFieldUser(clock, Tenant, "Karim", "Trabelsi", "EMP-042", "804613", [new(Roles.Operator, Site)], "admin").Value;
        user.ClearDomainEvents();
        return user;
    }
}
