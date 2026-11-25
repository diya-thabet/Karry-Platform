using Karry.BuildingBlocks.Outbox;
using Karry.Modules.Integrity.Domain;
using Karry.Modules.Notifications.Contracts;

namespace Karry.Modules.Integrity.Infrastructure;

/// <summary>
/// Flags people must hear of (ADR-0039 §2): a fraud suspicion (RULE-FO-11) to executives and company administrators,
/// a missing end (RULE-FO-08) to the site's managers. Other rules wait for the console.
/// </summary>
internal sealed class FlagNotices(IEnumerable<INotices> notices) : IDomainEventSubscriber<FlagOpened>
{
    public async Task HandleAsync(FlagOpened domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var type = domainEvent.Rule switch
        {
            IntegrityFlag.FraudRule => NoticeTypes.FraudSuspected,
            "RULE-FO-08" => NoticeTypes.MissingEnd,
            _ => null,
        };
        if (type is null)
        {
            return;
        }

        var (connection, transaction) = DomainEventTransaction.Of(context);
        var notice = new Notice(context.MessageId, type, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["flagId"] = domainEvent.FlagId.ToString("D"),
            ["subjectType"] = domainEvent.SubjectType,
            ["subjectId"] = domainEvent.SubjectId.ToString("D"),
        }, "/assets/integrity", domainEvent.SiteId);
        foreach (var inbox in notices)
        {
            await inbox.PublishAsync(connection, transaction, notice, cancellationToken).ConfigureAwait(false);
        }
    }
}
