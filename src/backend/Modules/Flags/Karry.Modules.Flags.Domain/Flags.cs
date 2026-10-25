using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Flags;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Flags.Domain;

public readonly record struct TenantFlagId(Guid Value) : IEntityId<TenantFlagId>
{
    public static TenantFlagId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>A company's (or one of its users') flag value changed; <c>After</c> null: the override was removed.</summary>
[EventType("flags.tenant_flag_changed.v1")]
public sealed record TenantFlagChanged(string Key, string? Subject, bool? Before, bool? After, string? Actor) : DomainEvent;

/// <summary>
/// A flag override for one company, or for one of its users (<see cref="Subject"/>: the token subject; empty for the
/// whole company). Tenant-owned, so every change is in that company's ledger (TC-P1-PLT-009). A removed override keeps
/// its row with no value.
/// </summary>
public sealed class TenantFlag : AggregateRoot<TenantFlagId>, ITenantOwned
{
    public const int SubjectMaxLength = 255;

    private TenantFlag(TenantFlagId id, TenantId tenantId, string key, string subject)
        : base(id)
    {
        TenantId = tenantId;
        Key = key;
        Subject = subject;
    }

    public TenantId TenantId { get; private set; }

    public string Key { get; private set; }

    /// <summary>The user's token subject, or empty for the whole company.</summary>
    public string Subject { get; private set; }

    public bool? Value { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public static Result<TenantFlag> Create(TimeProvider clock, TenantId tenant, string? key, string? subject, bool? value, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (FlagRules.Validate(key, subject) is { } invalid)
        {
            return invalid;
        }

        var flag = new TenantFlag(EntityId.New<TenantFlagId>(clock), tenant, key!, subject?.Trim() ?? string.Empty);
        flag.Change(clock, value, actor);
        return flag;
    }

    /// <summary>Sets or removes the value; false when nothing changes (no ledger entry).</summary>
    public bool Change(TimeProvider clock, bool? value, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Value == value && ChangedAt != default)
        {
            return false;
        }

        var before = ChangedAt == default ? null : Value;
        Value = value;
        ChangedAt = clock.GetUtcNow();
        Raise(new TenantFlagChanged(Key, Subject.Length == 0 ? null : Subject, before, value, actor) { OccurredAt = ChangedAt });
        return true;
    }
}

/// <summary>
/// The platform-wide value of a flag in this environment (each environment has its own database): the kill switch.
/// Platform data, changed by platform operators; every change is also a <see cref="PlatformFlagChange"/>.
/// </summary>
public sealed class PlatformFlag
{
    private PlatformFlag(string key) => Key = key;

    public string Key { get; private set; }

    public bool? Value { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public string? ChangedBy { get; private set; }

    public static PlatformFlag For(string key) => new(key);

    /// <summary>Sets or removes the value; the history entry to add, or null when nothing changes.</summary>
    public PlatformFlagChange? Change(TimeProvider clock, bool? value, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Value == value && ChangedAt != default)
        {
            return null;
        }

        var change = new PlatformFlagChange(EntityId.New<TenantFlagId>(clock).Value, Key, ChangedAt == default ? null : Value, value, actor, clock.GetUtcNow());
        Value = value;
        ChangedAt = change.At;
        ChangedBy = actor;
        return change;
    }
}

/// <summary>
/// One change of a platform-wide flag value. Append-only (the database refuses UPDATE and DELETE): the audit of
/// platform changes until there is a platform ledger chain.
/// </summary>
public sealed record PlatformFlagChange(Guid Id, string Key, bool? Before, bool? After, string? Actor, DateTimeOffset At);

/// <summary>Where a flag value came from, most specific first.</summary>
public enum FlagSource
{
    User,
    Tenant,
    Platform,
    Environment,
    Default,
}

/// <summary>
/// Evaluation (US-P1-PLT-05 AC1): the user's override, else the company's, else the platform-wide value, else the
/// environment's configured value, else the declared default.
/// </summary>
public static class FlagRules
{
    public static (bool Value, FlagSource Source) Evaluate(FlagDefinition flag, bool? environment, bool? platform, bool? tenant, bool? user)
    {
        ArgumentNullException.ThrowIfNull(flag);
        return user is { } u ? (u, FlagSource.User)
            : tenant is { } t ? (t, FlagSource.Tenant)
            : platform is { } p ? (p, FlagSource.Platform)
            : environment is { } e ? (e, FlagSource.Environment)
            : (flag.Default, FlagSource.Default);
    }

    public static Error? Validate(string? key, string? subject)
    {
        if (FeatureFlags.Find(key) is null)
        {
            return Error.Validation("flags.flag_unknown", $"Unknown flag '{key}': flags are declared in Karry.SharedKernel.Flags.FeatureFlags.");
        }

        var trimmed = subject?.Trim() ?? string.Empty;
        return trimmed.Length > TenantFlag.SubjectMaxLength || trimmed.Any(char.IsControl)
            ? Error.Validation("flags.subject_invalid", $"A user subject is at most {TenantFlag.SubjectMaxLength} characters.")
            : null;
    }
}
