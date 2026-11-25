using Karry.BuildingBlocks.Outbox;
using Karry.Modules.FieldOps.Domain;
using Karry.Modules.Notifications.Contracts;

namespace Karry.Modules.FieldOps.Infrastructure;

/// <summary>A machine held as unsafe (ADR-0035): its site's managers and maintenance planners hear of it (ADR-0039 §2).</summary>
internal sealed class HoldNotices(IEnumerable<INotices> notices) : IDomainEventSubscriber<AssetMarkedUnsafe>
{
    public async Task HandleAsync(AssetMarkedUnsafe domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var (connection, transaction) = DomainEventTransaction.Of(context);
        var notice = new Notice(context.MessageId, NoticeTypes.AssetUnsafe, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["assetId"] = domainEvent.AssetId.ToString("D"),
            ["items"] = string.Join(',', domainEvent.Items),
        }, "/assets/holds", domainEvent.SiteId);
        foreach (var inbox in notices)
        {
            await inbox.PublishAsync(connection, transaction, notice, cancellationToken).ConfigureAwait(false);
        }
    }
}
