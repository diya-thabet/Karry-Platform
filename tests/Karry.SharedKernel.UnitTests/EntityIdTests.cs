using Karry.SharedKernel.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Karry.SharedKernel.UnitTests;

[Trait("Story", "US-P1-PLT-01")]
[Trait("TestCase", "TC-P1-PLT-017")]
public sealed class EntityIdTests
{
    private readonly record struct AssetId(Guid Value) : IEntityId<AssetId>
    {
        public static AssetId From(Guid value) => new(value);
    }

    [Fact]
    public void New_ids_are_uuid_v7_and_sort_by_creation_time()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero));
        var ids = new List<AssetId>();
        for (var i = 0; i < 100; i++)
        {
            ids.Add(EntityId.New<AssetId>(clock));
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        ids.Should().AllSatisfy(id => id.Value.Version.Should().Be(7));
        ids.Select(id => id.Value.ToString()).Should().BeInAscendingOrder(StringComparer.Ordinal);
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Ids_created_in_the_same_millisecond_are_still_unique()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56));

        Enumerable.Range(0, 10_000).Select(_ => EntityId.New<AssetId>(clock)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Parse_rejects_the_empty_guid_and_non_v7_guids()
    {
        ((Action)(() => EntityId.Parse<AssetId>(Guid.Empty))).Should().Throw<ArgumentException>().WithMessage("*empty*");
        ((Action)(() => EntityId.Parse<AssetId>(Guid.NewGuid()))).Should().Throw<ArgumentException>().WithMessage("*UUIDv7*version 4*");
    }

    [Fact]
    public void Parse_accepts_a_v7_guid_and_round_trips()
    {
        var value = Guid.CreateVersion7();

        EntityId.Parse<AssetId>(value).Value.Should().Be(value);
    }
}
