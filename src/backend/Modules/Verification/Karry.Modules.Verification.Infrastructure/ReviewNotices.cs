using System.Globalization;
using Karry.BuildingBlocks.Outbox;
using Karry.Modules.Notifications.Contracts;
using Karry.Modules.Verification.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Verification.Infrastructure;

/// <summary>
/// Notices of review decisions (ADR-0039 §2): a rejection to the entry's operator (on the tablet), an escalation to
/// the policy's escalation role on the site; handed over in the dispatch's transaction.
/// </summary>
internal sealed class ReviewNotices(IEnumerable<INotices> notices) : IDomainEventSubscriber<ReviewItemRejected>, IDomainEventSubscriber<ReviewItemEscalated>
{
    public async Task HandleAsync(ReviewItemRejected domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(context);
        var item = await ((VerificationDbContext)context.Db).Items.AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == new ReviewItemId(domainEvent.ItemId), cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        await PublishAsync(context, new Notice(context.MessageId, NoticeTypes.ReviewRejected, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reasonCode"] = domainEvent.ReasonCode,
            ["entryId"] = domainEvent.RecordId.ToString("D"),
        }, "/history", domainEvent.SiteId)
        { Person = item.ProducedBy }, cancellationToken).ConfigureAwait(false);
    }

    public Task HandleAsync(ReviewItemEscalated domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return PublishAsync(context, new Notice(context.MessageId, NoticeTypes.ReviewEscalated, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dueAt"] = domainEvent.DueAt.ToString("O", CultureInfo.InvariantCulture),
            ["entryId"] = domainEvent.RecordId.ToString("D"),
        }, "/review", domainEvent.SiteId)
        { Role = domainEvent.EscalateTo }, cancellationToken);
    }

    private async Task PublishAsync(DomainEventContext context, Notice notice, CancellationToken cancellationToken)
    {
        var (connection, transaction) = DomainEventTransaction.Of(context);
        foreach (var inbox in notices)
        {
            await inbox.PublishAsync(connection, transaction, notice, cancellationToken).ConfigureAwait(false);
        }
    }
}
