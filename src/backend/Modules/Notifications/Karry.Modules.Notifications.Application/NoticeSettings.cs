using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Notifications.Domain;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Notifications.Application;

/// <summary>Notifications' permission (ADR-0039 §2).</summary>
public static class NotificationsPermissions
{
    /// <summary>
    /// Switch the company's notice rules on or off. Switching one off silences an alert for everyone (a fraud
    /// suspicion, a machine held as unsafe), so it is a company administrator's, privileged, with a recent sign-in.
    /// </summary>
    public const string RulesManage = "notifications.rules.manage";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(RulesManage, ScopeKind.Tenant, privileged: true, Roles.TenantAdmin),
    ];
}

/// <summary>A notice rule as the company has it: on or off, its channels, and who switched it last.</summary>
public sealed record NoticeRuleView(string Type, bool Enabled, IReadOnlyList<string> Channels, DateTimeOffset? ChangedAt, string? ChangedBy);

/// <summary>A person's choice for one notice type: each channel they may turn off, and whether it is on.</summary>
public sealed record NoticePreferenceView(string Type, bool RuleEnabled, IReadOnlyDictionary<string, bool> Channels);

/// <summary>
/// The company's rule switches and each person's channel choices (ADR-0039 §2). A person changes only their own; the
/// in-app centre cannot be turned off (it is where every notice is kept).
/// </summary>
public sealed class NoticeSettings(INotificationStore store, INoticeAudiences audiences, ITenantContext tenant, ICallerAccess caller, TimeProvider clock)
{
    public static readonly Error UnknownType = Error.NotFound("notifications.unknown_type", "No such notice type.");

    public static readonly Error ChannelInvalid = Error.Validation(
        "notifications.channel_invalid", "This notice type is not sent by that channel, or the channel cannot be turned off.");

    public static readonly Error NoAccount = Error.Forbidden("notifications.no_account", "Only a person of this company has notification preferences.");

    public async Task<IReadOnlyList<NoticeRuleView>> RulesAsync(CancellationToken cancellationToken)
    {
        var settings = (await store.RuleSettingsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(s => s.Type, StringComparer.Ordinal);
        return
        [
            .. NoticeRules.Types.Select(type =>
            {
                var setting = settings.GetValueOrDefault(type);
                return new NoticeRuleView(type, setting?.Enabled ?? true, NoticeRules.For(type)!.Channels, setting?.ChangedAt, setting?.ChangedBy);
            }),
        ];
    }

    /// <summary>
    /// Switches a rule. Switching one off needs a recent sign-in (it silences an alert); switching it back on does
    /// not. Each change is ledgered with who made it.
    /// </summary>
    public async Task<Result<NoticeRuleView>> SwitchAsync(string type, bool enabled, CancellationToken cancellationToken)
    {
        if (NoticeRules.For(type) is not { } rule)
        {
            return UnknownType;
        }

        if (!enabled && StepUp.Check(caller, clock) is { } stepUp)
        {
            return stepUp;
        }

        var company = tenant.Current ?? throw new InvalidOperationException("Rules belong to a tenant.");
        var setting = await store.RuleSettingAsync(type, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            setting = NoticeRuleSetting.For(clock, company, type);
            if (setting.Switch(clock, enabled, caller.Subject))
            {
                store.Add(setting);
            }
        }
        else
        {
            setting.Switch(clock, enabled, caller.Subject);
        }

        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return new NoticeRuleView(type, setting.Enabled, rule.Channels, setting.ChangedBy is null ? null : setting.ChangedAt, setting.ChangedBy);
    }

    /// <summary>The caller's choices: for each type, the channels they may turn off and whether each is on.</summary>
    public async Task<Result<IReadOnlyList<NoticePreferenceView>>> MineAsync(CancellationToken cancellationToken)
    {
        if (await MeAsync(cancellationToken).ConfigureAwait(false) is not { } me)
        {
            return NoAccount;
        }

        var rules = (await store.RuleSettingsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(s => s.Type, s => s.Enabled, StringComparer.Ordinal);
        var mine = await store.PreferencesOfAsync(me, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<NoticePreferenceView> views =
        [
            .. NoticeRules.Types.Select(type => new NoticePreferenceView(
                type,
                rules.GetValueOrDefault(type, true),
                NoticeRules.For(type)!.Channels.Where(NoticeChannels.Optional.Contains).ToDictionary(
                    channel => channel,
                    channel => mine.FirstOrDefault(p => p.Type == type && p.Channel == channel)?.Enabled ?? true,
                    StringComparer.Ordinal))),
        ];
        return Result.Success(views);
    }

    /// <summary>Turns one of the caller's channels on or off for a notice type.</summary>
    public async Task<Result<IReadOnlyList<NoticePreferenceView>>> SetMineAsync(string type, string channel, bool enabled, CancellationToken cancellationToken)
    {
        if (NoticeRules.For(type) is not { } rule)
        {
            return UnknownType;
        }

        if (!NoticeChannels.Optional.Contains(channel) || !rule.Channels.Contains(channel))
        {
            return ChannelInvalid;
        }

        if (await MeAsync(cancellationToken).ConfigureAwait(false) is not { } me)
        {
            return NoAccount;
        }

        var company = tenant.Current ?? throw new InvalidOperationException("Preferences belong to a tenant.");
        var existing = (await store.PreferencesOfAsync(me, cancellationToken).ConfigureAwait(false)).FirstOrDefault(p => p.Type == type && p.Channel == channel);
        if (existing is null)
        {
            store.Add(NoticePreference.For(clock, company, me, type, channel, enabled));
        }
        else
        {
            existing.Set(clock, enabled);
        }

        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return await MineAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<Guid?> MeAsync(CancellationToken cancellationToken) =>
        caller.Subject is { } subject ? audiences.UserIdOfAsync(subject, cancellationToken) : Task.FromResult<Guid?>(null);
}
