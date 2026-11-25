using Karry.Modules.Notifications.Contracts;
using Karry.Modules.Notifications.Domain;

namespace Karry.Modules.Notifications.Tests;

/// <summary>The values the notification centre pages and words by (ADR-0039 §1, §2).</summary>
[Trait("Story", "US-P2-NOT-01")]
public sealed class NotificationValuesTests
{
    [Fact]
    public void Notification_ids_order_by_creation_so_the_list_pages_newest_first()
    {
        var older = NotificationId.From(Guid.CreateVersion7(DateTimeOffset.UtcNow.AddSeconds(-1)));
        var newer = NotificationId.From(Guid.CreateVersion7(DateTimeOffset.UtcNow));
        var same = NotificationId.From(older.Value);

        bool[] holds = [older < newer, newer > older, older <= same, older >= same];
        bool[] fails = [newer <= older, older >= newer, newer < older, older > newer];

        holds.Should().AllBeEquivalentTo(true);
        fails.Should().AllBeEquivalentTo(false);
        older.ToString().Should().Be(older.Value.ToString("D"), "an ID is written as a plain UUID in links and cursors");
    }

    [Fact]
    public void Every_notice_type_is_listed_once_and_named_by_its_module()
    {
        NoticeTypes.All.Should().OnlyHaveUniqueItems().And.HaveCount(5)
            .And.OnlyContain(t => t.Split('.').Length == 2, "module.event: the words of each type live under its key in every language");
    }
}
