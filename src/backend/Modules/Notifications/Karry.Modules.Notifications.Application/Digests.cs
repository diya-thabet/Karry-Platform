using System.Globalization;
using System.Text;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Notifications.Domain;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Tenancy.Contracts;
using Karry.Modules.Verification.Contracts;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Karry.Modules.Notifications.Application;

/// <summary>
/// The daily e-mail digest (ADR-0039 §4, US-P2-VER-06 AC2, TC-P2-NOT-003): at 07:00 in each site's time zone, every
/// manager of the site and every person with digest notices about it gets one e-mail for that site and day: the site's
/// review counts (managers only: waiting, overdue, corrected in the last day) and their digest notices of the day
/// before, counted by kind, with links to the portal. Counts and links only, never an entry's values; in French then
/// English (the reader's language is not known to the server). Nothing to say, nothing sent.
/// </summary>
public sealed class DailyDigests(
    INotificationStore store,
    INoticeAudiences audiences,
    ISiteDirectory sites,
    IReviewCounts reviews,
    ITenantDirectory tenants,
    IAppAddresses addresses,
    ITenantContext tenant,
    TimeProvider clock)
{
    public const string Kind = "digest";

    /// <summary>The local time the digest is due; it is still made until <see cref="LastHour"/> if the job was down at 07:00.</summary>
    public static readonly LocalTime Due = new(7, 0);

    public const int LastHour = 12;

    /// <summary>The role that receives a site's review counts (US-P2-VER-06 AC2).</summary>
    public const string ManagerRole = "site-manager";

    /// <summary>The counts follow the choices for overdue reviews: the same work, and the same switch to turn it off.</summary>
    public const string CountsType = "review.escalated";

    /// <summary>The notice types e-mailed in the digest: those whose rule uses the digest channel.</summary>
    public static IReadOnlyList<string> DigestTypes { get; } =
        [.. NoticeRules.Types.Where(type => NoticeRules.For(type)!.Channels.Contains(NoticeChannels.Digest))];

    /// <summary>Queues the digests due at <paramref name="now"/> in the current company; returns how many were queued.</summary>
    public async Task<int> QueueDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var company = tenant.Current ?? throw new InvalidOperationException("Digests are made in a tenant.");
        if (await tenants.FindAsync(company, cancellationToken).ConfigureAwait(false) is not { } summary)
        {
            return 0;
        }

        var portal = addresses.PortalFor(summary.Slug);
        var switchedOff = (await store.RuleSettingsAsync(cancellationToken).ConfigureAwait(false))
            .Where(s => !s.Enabled).Select(s => s.Type).ToHashSet(StringComparer.Ordinal);
        var queued = 0;
        foreach (var site in (await sites.ListAsync(cancellationToken).ConfigureAwait(false)).Where(s => !s.Archived))
        {
            if (Window(site.TimeZone, now) is not { } window)
            {
                continue;
            }

            queued += await QueueSiteAsync(company, site, window, now, portal, switchedOff, cancellationToken).ConfigureAwait(false);
        }

        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return queued;
    }

    /// <summary>
    /// When the site's digest of today is due at <paramref name="now"/> (07:00 to 12:00 local): its day and the period it
    /// covers, from yesterday's 07:00 to today's (local, so a clock change is handled); otherwise null.
    /// </summary>
    public static DigestWindow? Window(string timeZone, DateTimeOffset now)
    {
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone) ?? DateTimeZone.Utc;
        var local = Instant.FromDateTimeOffset(now).InZone(zone);
        if (local.TimeOfDay < Due || local.Hour >= LastHour)
        {
            return null;
        }

        // As UTC instants: the database stores and compares UTC only (an offset is refused).
        var to = local.Date.At(Due).InZoneLeniently(zone).ToInstant().ToDateTimeOffset();
        var from = local.Date.PlusDays(-1).At(Due).InZoneLeniently(zone).ToInstant().ToDateTimeOffset();
        return new DigestWindow(local.Date.ToDateOnly(), from, to);
    }

    private async Task<int> QueueSiteAsync(
        TenantId company, SiteSummary site, DigestWindow window, DateTimeOffset now, Uri portal, HashSet<string> switchedOff, CancellationToken cancellationToken)
    {
        var types = DigestTypes.Where(type => !switchedOff.Contains(type)).ToList();
        var notices = types.Count == 0 ? [] : await store.SiteNoticesAsync(site.Id, types, window.From, window.To, cancellationToken).ConfigureAwait(false);
        var managers = (await audiences.HoldersAsync([ManagerRole], site.Id, cancellationToken).ConfigureAwait(false)).ToHashSet();
        var counts = switchedOff.Contains(CountsType) ? null : await reviews.OfSiteAsync(site.Id, window.From, now, cancellationToken).ConfigureAwait(false);

        var queued = 0;
        foreach (var person in managers.Concat(notices.Select(n => n.RecipientId)).Distinct())
        {
            var key = $"{Kind}:{person:N}:{site.Id:N}:{window.Day:yyyy-MM-dd}";
            if (await store.EmailQueuedAsync(key, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var off = (await store.PreferencesOfAsync(person, cancellationToken).ConfigureAwait(false))
                .Where(p => p.Channel == NoticeChannels.Digest && !p.Enabled).Select(p => p.Type).ToHashSet(StringComparer.Ordinal);
            var mine = notices.Where(n => n.RecipientId == person && !off.Contains(n.Type))
                .GroupBy(n => n.Type).Select(g => (Type: g.Key, Count: g.Count())).OrderBy(g => g.Type, StringComparer.Ordinal).ToList();
            var theirCounts = managers.Contains(person) && !off.Contains(CountsType) && counts is { } c && c.Pending + c.Overdue + c.Corrected > 0 ? counts : null;
            if (mine.Count == 0 && theirCounts is null)
            {
                continue;
            }

            store.Add(EmailDelivery.Queue(clock, company, person, Kind, key, Subject(site, window.Day), Text(site, window.Day, theirCounts, mine, portal)));
            queued++;
        }

        return queued;
    }

    internal static string Subject(SiteSummary site, DateOnly day) =>
        $"Karry · {site.Code} · {day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    /// <summary>The digest's text: counts and links, in French then English.</summary>
    public static string Text(SiteSummary site, DateOnly day, ReviewCounts? counts, IReadOnlyList<(string Type, int Count)> notices, Uri portal)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(notices);
        var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var review = new Uri(portal, "/review");
        var centre = new Uri(portal, "/notifications");
        var text = new StringBuilder();
        Section(text, $"Résumé du {date} pour {site.Code} · {site.Name}", counts,
            ("Saisies à contrôler", "Dont en retard", "Corrigées depuis hier 07:00"), notices, French, review, centre, "Ouvrir le contrôle", "Toutes vos notifications");
        text.AppendLine().AppendLine("---").AppendLine();
        Section(text, $"Digest of {date} for {site.Code} · {site.Name}", counts,
            ("Entries to review", "Of which overdue", "Corrected since yesterday 07:00"), notices, English, review, centre, "Open the review queue", "All your notifications");
        return text.ToString().TrimEnd();
    }

    private static void Section(
        StringBuilder text, string title, ReviewCounts? counts, (string Pending, string Overdue, string Corrected) labels,
        IReadOnlyList<(string Type, int Count)> notices, IReadOnlyDictionary<string, string> names, Uri review, Uri centre, string openReview, string openCentre)
    {
        text.AppendLine(title).AppendLine();
        if (counts is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {labels.Pending} : {counts.Pending}");
            text.AppendLine(CultureInfo.InvariantCulture, $"- {labels.Overdue} : {counts.Overdue}");
            text.AppendLine(CultureInfo.InvariantCulture, $"- {labels.Corrected} : {counts.Corrected}");
            text.AppendLine(CultureInfo.InvariantCulture, $"{openReview} : {review}").AppendLine();
        }

        if (notices.Count > 0)
        {
            foreach (var (type, count) in notices)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {names.GetValueOrDefault(type, type)} : {count}");
            }

            text.AppendLine(CultureInfo.InvariantCulture, $"{openCentre} : {centre}");
        }
    }

    private static readonly Dictionary<string, string> French = new(StringComparer.Ordinal)
    {
        ["review.escalated"] = "Contrôles en retard qui vous sont remontés",
        ["integrity.fraud_suspected"] = "Soupçons de fraude",
        ["integrity.missing_end"] = "Fins de poste manquantes",
    };

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["review.escalated"] = "Overdue reviews escalated to you",
        ["integrity.fraud_suspected"] = "Fraud suspicions",
        ["integrity.missing_end"] = "Missing shift ends",
    };
}

/// <summary>A site's digest day and the period it covers (yesterday 07:00 to today 07:00, local).</summary>
public sealed record DigestWindow(DateOnly Day, DateTimeOffset From, DateTimeOffset To);

/// <summary>What a run of the digests did: digests queued, and companies whose run failed.</summary>
public sealed record DigestRun(int Queued, int Failed);

/// <summary>Queues the due digests company by company (every 15 minutes); one company's failure does not stop the others.</summary>
public sealed partial class QueueDigests(IServiceScopeFactory scopes, TimeProvider clock, ILogger<QueueDigests> logger)
{
    public Task<DigestRun> RunAsync(CancellationToken cancellationToken) => RunAtAsync(clock.GetUtcNow(), cancellationToken);

    public async Task<DigestRun> RunAtAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantId> companies;
        await using (var scope = scopes.CreateAsyncScope())
        {
            companies = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken).ConfigureAwait(false);
        }

        var queued = 0;
        var failed = 0;
        foreach (var company in companies)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(company);
                queued += await scope.ServiceProvider.GetRequiredService<DailyDigests>().QueueDueAsync(now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogFailed(logger, ex, company.Value);
            }
        }

        return new DigestRun(queued, failed);
    }

    [LoggerMessage(EventId = 7602, Level = LogLevel.Error, Message = "Queueing the daily digests of tenant {Tenant} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid tenant);
}
