using Karry.Modules.Trust.Domain;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Trust.Tests;

/// <summary>
/// The hash specification against golden values computed by an independent implementation (Python hashlib, from the
/// text of the specification in <see cref="LedgerHash"/>), never by re-running the code under test.
/// </summary>
[Trait("Story", "US-P1-AUD-01")]
public sealed class LedgerHashTests
{
    internal static readonly TenantId Tenant = new(Guid.Parse("01970000-0000-7000-8000-00000000000a"));
    internal const string Payload = """{"tenantId":"01970000-0000-7000-8000-00000000000a","slug":"carriere-sfax","name":"Carrière de Sfax","eventId":"01970000-0000-7000-8000-0000000000e1","occurredAt":"2026-10-23T08:00:00.1234567+00:00"}""";
    internal static readonly DateTimeOffset OccurredAt = new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero).AddTicks(1234567);

    private const string PayloadHash = "9b4595fad473ed803b6bacab65c8b470b8615386672285e140ed289544ada82a";
    private const string FirstEntry = "0a7404bf72ac3f6cefa3b32c8ba59dd7aca7bcc4ae7b45cb8bd3edd70b8937c3";
    private const string SecondEntry = "c3ffaaa432fbed292f765516e07f092449267a29a4b2f9c0ea4223e4d995ede1";

    [Fact]
    public void Payload_and_entry_hashes_match_an_independent_implementation()
    {
        var canonical = LedgerHash.CanonicalPayload(Payload);

        LedgerHash.OfPayload(canonical).Should().Be(PayloadHash);
        LedgerHash.OfEntry(LedgerHash.Genesis, Tenant, 1, "tenancy.tenant_provisioned.v1", PayloadHash, OccurredAt).Should().Be(FirstEntry);
        LedgerHash.OfEntry(FirstEntry, Tenant, 2, "tenancy.tenant_provisioned.v1", PayloadHash, OccurredAt).Should().Be(SecondEntry);
    }

    [Fact]
    public void Timestamps_are_hashed_at_microsecond_precision_like_postgresql_stores_them()
    {
        LedgerHash.Timestamp(OccurredAt).Should().Be("2026-10-23T08:00:00.123456Z");
        LedgerHash.Timestamp(OccurredAt.ToOffset(TimeSpan.FromHours(1))).Should().Be("2026-10-23T08:00:00.123456Z", "always UTC");
        LedgerHash.OfEntry(LedgerHash.Genesis, Tenant, 1, "t.e.v1", PayloadHash, OccurredAt)
            .Should().Be(LedgerHash.OfEntry(LedgerHash.Genesis, Tenant, 1, "t.e.v1", PayloadHash, LedgerHash.ToStoragePrecision(OccurredAt)));
    }

    [Fact]
    public void Every_field_is_part_of_the_hash()
    {
        var baseline = LedgerHash.OfEntry(LedgerHash.Genesis, Tenant, 1, "tenancy.tenant_provisioned.v1", PayloadHash, OccurredAt);

        string[] variants =
        [
            LedgerHash.OfEntry(FirstEntry, Tenant, 1, "tenancy.tenant_provisioned.v1", PayloadHash, OccurredAt),
            LedgerHash.OfEntry(LedgerHash.Genesis, new TenantId(Guid.CreateVersion7()), 1, "tenancy.tenant_provisioned.v1", PayloadHash, OccurredAt),
            LedgerHash.OfEntry(LedgerHash.Genesis, Tenant, 2, "tenancy.tenant_provisioned.v1", PayloadHash, OccurredAt),
            LedgerHash.OfEntry(LedgerHash.Genesis, Tenant, 1, "tenancy.tenant_renamed.v1", PayloadHash, OccurredAt),
            LedgerHash.OfEntry(LedgerHash.Genesis, Tenant, 1, "tenancy.tenant_provisioned.v1", SecondEntry, OccurredAt),
            LedgerHash.OfEntry(LedgerHash.Genesis, Tenant, 1, "tenancy.tenant_provisioned.v1", PayloadHash, OccurredAt.AddTicks(10)),
        ];

        variants.Should().OnlyHaveUniqueItems().And.NotContain(baseline);
    }

    [Fact]
    public void Sequences_start_at_one_and_event_types_cannot_inject_a_separator()
    {
        ((Action)(() => LedgerHash.OfEntry(LedgerHash.Genesis, Tenant, 0, "t.e.v1", PayloadHash, OccurredAt))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => LedgerHash.OfEntry(LedgerHash.Genesis, Tenant, 1, "t.e.v1\nforged", PayloadHash, OccurredAt))).Should().Throw<ArgumentException>();
    }
}
