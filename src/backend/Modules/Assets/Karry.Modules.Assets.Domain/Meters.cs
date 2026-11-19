using System.Globalization;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Assets.Domain;

public readonly record struct MeterId(Guid Value) : IEntityId<MeterId>
{
    public static MeterId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>A meter's state (US-P1-AST-03 AC1). Replacement (a new meter with an offset) is Phase 2.</summary>
public enum MeterStatus
{
    Ok = 1,

    /// <summary>Not counting: no reading is accepted until it is repaired (Phase 2 estimates usage meanwhile).</summary>
    Failed = 2,

    Replaced = 3,
}

/// <summary>
/// One reading of a meter (US-P1-AST-03 AC2): the value shown at a moment. Rows are append-only (trigger and grants):
/// a wrong reading is corrected by a later one, never by an edit.
/// </summary>
public sealed class MeterReading : ITenantOwned
{
    internal MeterReading(Guid id, TenantId tenantId, MeterId meterId, decimal value, DateTimeOffset readAt, DateTimeOffset recordedAt, string? recordedBy)
    {
        Id = id;
        TenantId = tenantId;
        MeterId = meterId;
        Value = value;
        ReadAt = readAt;
        RecordedAt = recordedAt;
        RecordedBy = recordedBy;
    }

    // EF Core
    private MeterReading()
    {
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public MeterId MeterId { get; private set; }

    public decimal Value { get; private set; }

    /// <summary>When the meter showed <see cref="Value"/>.</summary>
    public DateTimeOffset ReadAt { get; private set; }

    /// <summary>When the reading reached the server.</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    public string? RecordedBy { get; private set; }
}

[EventType("assets.meter_added.v1")]
[AuditSubject("asset", nameof(AssetId))]
public sealed record MeterAdded(Guid MeterId, Guid AssetId, string Kind, decimal InitialReading, DateOnly InstalledOn, string? Actor) : DomainEvent;

[EventType("assets.meter_reading_recorded.v1")]
[AuditSubject("asset", nameof(AssetId))]
public sealed record MeterReadingRecorded(Guid MeterId, Guid AssetId, Guid ReadingId, decimal Value, DateTimeOffset ReadAt, string? Actor) : DomainEvent;

/// <summary>What happened to a meter (ADR-0036 §5): a manager's decision, recorded with a reason and photos.</summary>
public enum MeterEventKind
{
    /// <summary>A new meter in place of the old one: the old one's final value, the new one's initial value.</summary>
    Replaced = 1,

    /// <summary>The meter went back (rollover, reset): the value before, the value after.</summary>
    Reset = 2,

    /// <summary>The meter stopped counting: entries are estimated until it is repaired.</summary>
    Failed = 3,

    /// <summary>The meter counts again from the value read at the repair.</summary>
    Repaired = 4,
}

/// <summary>
/// One meter event (US-P2-AST-01, 02; ADR-0036 §5). Rows are append-only (trigger and grants): a wrong event is
/// explained by a later one, never edited. REPLACED and RESET carry both values (usage stays continuous: the meter's
/// offset grows by before − after); FAILED may carry the last value shown; REPAIRED carries the value read at the repair.
/// </summary>
public sealed class MeterEvent : ITenantOwned
{
    internal MeterEvent(
        Guid id, TenantId tenantId, MeterId meterId, AssetId assetId, MeterEventKind kind, DateTimeOffset occurredAt, decimal? valueBefore, decimal? valueAfter,
        string reason, IReadOnlyList<string> photos, DateTimeOffset recordedAt, string? recordedBy)
    {
        Id = id;
        TenantId = tenantId;
        MeterId = meterId;
        AssetId = assetId;
        Kind = kind;
        OccurredAt = occurredAt;
        ValueBefore = valueBefore;
        ValueAfter = valueAfter;
        Reason = reason;
        Photos = photos;
        RecordedAt = recordedAt;
        RecordedBy = recordedBy;
    }

    // EF Core
    private MeterEvent()
    {
        Reason = string.Empty;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public MeterId MeterId { get; private set; }

    public AssetId AssetId { get; private set; }

    public MeterEventKind Kind { get; private set; }

    /// <summary>When it happened on the machine (not when it was recorded).</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    public decimal? ValueBefore { get; private set; }

    public decimal? ValueAfter { get; private set; }

    public string Reason { get; private set; }

    /// <summary>The photos that show it, as <c>sha256:</c> references (evidence).</summary>
    public IReadOnlyList<string> Photos { get; private set; } = [];

    public DateTimeOffset RecordedAt { get; private set; }

    public string? RecordedBy { get; private set; }
}

[EventType("assets.meter_event_recorded.v1")]
[AuditSubject("asset", nameof(AssetId))]
public sealed record MeterEventRecorded(
    Guid MeterId, Guid AssetId, Guid MeterEventId, string Kind, DateTimeOffset HappenedAt, decimal? ValueBefore, decimal? ValueAfter, string Reason, IReadOnlyList<string> Photos, string? Actor)
    : DomainEvent;

[EventType("assets.meter_status_changed.v1")]
[AuditSubject("asset", nameof(AssetId))]
public sealed record MeterStatusChanged(Guid MeterId, Guid AssetId, string From, string To, string? Actor) : DomainEvent;

/// <summary>
/// An hour or kilometre meter of an asset (US-P1-AST-03). Its readings are kept apart (append-only, many per meter):
/// the meter holds the last one, so a new reading is checked without loading the history.
/// </summary>
public sealed class Meter : AggregateRoot<MeterId>, ITenantOwned
{
    private readonly List<MeterReading> pending = [];
    private readonly List<MeterEvent> pendingEvents = [];

    private Meter(MeterId id, TenantId tenantId, AssetId assetId, MeterKind kind, decimal initialReading, DateOnly installedOn, DateTimeOffset at)
        : base(id)
    {
        TenantId = tenantId;
        AssetId = assetId;
        Kind = kind;
        InitialReading = initialReading;
        InstalledOn = installedOn;
        Status = MeterStatus.Ok;
        CreatedAt = at;
        ChangedAt = at;
    }

    // EF Core
    private Meter()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    public AssetId AssetId { get; private set; }

    public MeterKind Kind { get; private set; }

    /// <summary>The value shown when the meter was installed (a second-hand machine does not start at 0).</summary>
    public decimal InitialReading { get; private set; }

    public DateOnly InstalledOn { get; private set; }

    public MeterStatus Status { get; private set; }

    /// <summary>Added to the readings of a replacement meter so usage stays continuous (Phase 2); 0 until then.</summary>
    public decimal Offset { get; private set; }

    public decimal LastValue { get; private set; }

    public DateTimeOffset LastReadAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    /// <summary>Readings recorded since the meter was loaded, for the store to insert.</summary>
    public IReadOnlyList<MeterReading> PendingReadings => pending;

    /// <summary>Events recorded since the meter was loaded, for the store to insert.</summary>
    public IReadOnlyList<MeterEvent> PendingEvents => pendingEvents;

    /// <summary>Called by the store once <see cref="PendingReadings"/> and <see cref="PendingEvents"/> are saved.</summary>
    public void ClearPendingReadings()
    {
        pending.Clear();
        pendingEvents.Clear();
    }

    /// <summary>A new meter with its initial reading as the first of its history, read at the start of the installation day (UTC).</summary>
    public static Meter Add(TimeProvider clock, TenantId tenant, AssetId asset, MeterKind kind, decimal initialReading, DateOnly installedOn, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var at = clock.GetUtcNow();
        var meter = new Meter(EntityId.New<MeterId>(clock), tenant, asset, kind, initialReading, installedOn, at);
        meter.Raise(new MeterAdded(meter.Id.Value, asset.Value, AssetRules.MeterKindCode(kind), initialReading, installedOn, actor) { OccurredAt = at });
        meter.Append(clock, initialReading, new DateTimeOffset(installedOn, TimeOnly.MinValue, TimeSpan.Zero), actor);
        return meter;
    }

    /// <summary>
    /// Records what the meter shows at <paramref name="readAt"/>: not before the last reading, neither in value nor in
    /// time, and not in the future. A failed or replaced meter takes no reading.
    /// </summary>
    public Result<MeterReading> Record(TimeProvider clock, decimal value, DateTimeOffset readAt, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status != MeterStatus.Ok)
        {
            return MeterRules.NotCounting;
        }

        if (MeterRules.Value(value) is { } invalid)
        {
            return invalid;
        }

        if (readAt > clock.GetUtcNow() + MeterRules.ClockTolerance)
        {
            return MeterRules.ReadInFuture;
        }

        if (readAt < LastReadAt)
        {
            return MeterRules.ReadBeforeLast(LastReadAt);
        }

        if (value < LastValue)
        {
            return MeterRules.Decreased(LastValue);
        }

        return Append(clock, value, readAt, actor);
    }

    /// <summary>OK or failed; false when unchanged. Replacement is Phase 2.</summary>
    public Result<bool> SetStatus(TimeProvider clock, MeterStatus status, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (status == MeterStatus.Replaced || Status == MeterStatus.Replaced)
        {
            return MeterRules.ReplacementLater;
        }

        if (status == Status)
        {
            return false;
        }

        var from = Status;
        Status = status;
        ChangedAt = clock.GetUtcNow();
        Raise(new MeterStatusChanged(Id.Value, AssetId.Value, MeterRules.StatusCode(from), MeterRules.StatusCode(status), actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>
    /// Records a meter event (ADR-0036 §5) after its checks: not before the last reading, not in the future; REPLACED and
    /// RESET need both values, the value before not below the last reading of a counting meter, and move the offset by
    /// before − after (the value after becomes the last reading, a failed meter counts again); FAILED needs a counting
    /// meter and stops it; REPAIRED needs a failed meter and a value not below the last reading, and makes it count again.
    /// </summary>
    public Result<MeterEvent> RecordEvent(
        TimeProvider clock, MeterEventKind kind, DateTimeOffset occurredAt, decimal? before, decimal? after, string? reason, IReadOnlyList<string>? photos, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == MeterStatus.Replaced)
        {
            return MeterRules.NotCounting;
        }

        if (MeterRules.EventReason(reason) is { } badReason)
        {
            return badReason;
        }

        if (MeterRules.EventPhotos(photos) is { } badPhotos)
        {
            return badPhotos;
        }

        if (occurredAt > clock.GetUtcNow() + MeterRules.ClockTolerance)
        {
            return MeterRules.ReadInFuture;
        }

        if (occurredAt < LastReadAt)
        {
            return MeterRules.ReadBeforeLast(LastReadAt);
        }

        foreach (var value in new[] { before, after })
        {
            if (value is { } v && MeterRules.Value(v) is { } invalid)
            {
                return invalid;
            }
        }

        switch (kind)
        {
            case MeterEventKind.Replaced or MeterEventKind.Reset:
                if (before is not { } final || after is not { } initial)
                {
                    return MeterRules.EventValuesMissing;
                }

                if (Status == MeterStatus.Ok && final < LastValue)
                {
                    return MeterRules.Decreased(LastValue);
                }

                Offset += final - initial;
                SetCounting(clock, actor);
                Append(clock, initial, occurredAt, actor);
                break;
            case MeterEventKind.Failed:
                if (Status != MeterStatus.Ok)
                {
                    return MeterRules.NotCounting;
                }

                if (before is { } shown && shown < LastValue)
                {
                    return MeterRules.Decreased(LastValue);
                }

                after = null;
                SetStatus(clock, MeterStatus.Failed, actor);
                break;
            case MeterEventKind.Repaired:
                if (Status != MeterStatus.Failed)
                {
                    return MeterRules.NotFailed;
                }

                if (after is not { } read)
                {
                    return MeterRules.EventValuesMissing;
                }

                if (read < LastValue)
                {
                    return MeterRules.Decreased(LastValue);
                }

                before = null;
                SetCounting(clock, actor);
                Append(clock, read, occurredAt, actor);
                break;
            default:
                return MeterRules.EventKindInvalid;
        }

        var recorded = new MeterEvent(
            EntityId.New<MeterId>(clock).Value, TenantId, Id, AssetId, kind, occurredAt, before, after, reason!.Trim(), [.. photos!], clock.GetUtcNow(), actor);
        pendingEvents.Add(recorded);
        ChangedAt = recorded.RecordedAt;
        Raise(new MeterEventRecorded(
            Id.Value, AssetId.Value, recorded.Id, MeterRules.EventKindCode(kind), occurredAt, before, after, recorded.Reason, recorded.Photos, actor)
        { OccurredAt = ChangedAt });
        return recorded;
    }

    private void SetCounting(TimeProvider clock, string? actor)
    {
        if (Status == MeterStatus.Failed)
        {
            SetStatus(clock, MeterStatus.Ok, actor);
        }
    }

    private MeterReading Append(TimeProvider clock, decimal value, DateTimeOffset readAt, string? actor)
    {
        var reading = new MeterReading(EntityId.New<MeterId>(clock).Value, TenantId, Id, value, readAt, clock.GetUtcNow(), actor);
        pending.Add(reading);
        LastValue = value;
        LastReadAt = readAt;
        ChangedAt = reading.RecordedAt;
        Raise(new MeterReadingRecorded(Id.Value, AssetId.Value, reading.Id, value, readAt, actor) { OccurredAt = ChangedAt });
        return reading;
    }
}

/// <summary>Meter rules (US-P1-AST-03): readings up to 9,999,999.9 with one decimal (tenths of an hour or of a kilometre).</summary>
public static class MeterRules
{
    public const decimal MaxValue = 9_999_999.9m;
    public const int Decimals = 1;

    /// <summary>A device clock a little ahead of the server is not a reading from the future.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    public static readonly Error KindInvalid = Error.Validation("assets.meter_kind_invalid", "A meter counts hours or km.");

    public static readonly Error StatusInvalid = Error.Validation("assets.meter_status_invalid", "The status is ok or failed.");

    public static readonly Error ReadInFuture = Error.Validation("assets.reading_in_future", "A reading cannot be dated in the future.");

    public static readonly Error NotCounting = Error.Conflict("assets.meter_not_counting", "This meter is failed or replaced; it takes no reading.");

    public static readonly Error EventOccurredAtMissing = Error.Validation("assets.meter_event_time", "Say when it happened.");

    public static readonly Error NotFailed = Error.Conflict("assets.meter_not_failed", "Only a failed meter is repaired.");

    public static readonly Error EventKindInvalid = Error.Validation("assets.meter_event_invalid", "The event is replaced, reset, failed or repaired.");

    public static readonly Error EventValuesMissing = Error.Validation(
        "assets.meter_event_values", "Replaced and reset need the value before and after; repaired needs the value read at the repair.");

    public const int MinReason = 10;
    public const int MaxReason = 500;
    public const int MaxPhotos = 5;

    public static readonly Error EventReasonInvalid = Error.Validation("assets.meter_event_reason", $"Say why, in {MinReason} to {MaxReason} characters.");

    public static readonly Error EventPhotosInvalid = Error.Validation("assets.meter_event_photos", $"One to {MaxPhotos} different photos, as sha256 references.");

    public static Error? EventReason(string? reason) =>
        reason?.Trim() is { Length: >= MinReason and <= MaxReason } ? null : EventReasonInvalid;

    public static Error? EventPhotos(IReadOnlyList<string>? photos) =>
        photos is { Count: >= 1 and <= MaxPhotos } && photos.All(IsPhotoReference) && photos.Distinct(StringComparer.Ordinal).Count() == photos.Count ? null : EventPhotosInvalid;

    /// <summary><c>sha256:</c> and 64 lowercase hex digits, as evidence is named everywhere.</summary>
    public static bool IsPhotoReference(string? reference) =>
        reference is { Length: 71 } && reference.StartsWith("sha256:", StringComparison.Ordinal) && reference.AsSpan(7).IndexOfAnyExcept(LowerHex) < 0;

    private static readonly System.Buffers.SearchValues<char> LowerHex = System.Buffers.SearchValues.Create("0123456789abcdef");

    public static Result<MeterEventKind> EventKind(string? kind) => kind switch
    {
        "replaced" => MeterEventKind.Replaced,
        "reset" => MeterEventKind.Reset,
        "failed" => MeterEventKind.Failed,
        "repaired" => MeterEventKind.Repaired,
        _ => EventKindInvalid,
    };

    public static string EventKindCode(MeterEventKind kind) => kind switch
    {
        MeterEventKind.Replaced => "replaced",
        MeterEventKind.Reset => "reset",
        MeterEventKind.Failed => "failed",
        MeterEventKind.Repaired => "repaired",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static readonly Error ReplacementLater = Error.BusinessRule(
        "assets.meter_replacement_unavailable", "Replacing a meter (a new one with an offset) comes with field operations.");

    public static Error ValueInvalid { get; } = Error.Validation(
        "assets.reading_invalid", $"A reading is from 0 to {MaxValue.ToString(CultureInfo.InvariantCulture)} with at most {Decimals} decimal.");

    public static Error ReadBeforeLast(DateTimeOffset last) =>
        Error.Validation("assets.reading_before_last", "A reading cannot be dated before the meter's last reading.").With("lastReadAt", last.ToString("O", CultureInfo.InvariantCulture));

    public static Error Decreased(decimal last) =>
        Error.Validation("assets.reading_decreased", "A meter never goes back: the reading is below the last one.").With("lastValue", last.ToString(CultureInfo.InvariantCulture));

    public static Error InstalledOnInvalid(DateOnly today) =>
        Error.Validation("assets.installed_on_invalid", $"The installation date is from {AssetRules.FirstYear}-01-01 to today.")
            .With("today", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>The error of an out-of-range value, or null.</summary>
    public static Error? Value(decimal value) => value < 0m || value > MaxValue || decimal.Round(value, Decimals) != value ? ValueInvalid : null;

    public static Result<MeterKind> Kind(string? kind) => kind switch
    {
        "hours" => MeterKind.Hours,
        "km" => MeterKind.Km,
        _ => KindInvalid,
    };

    public static Result<MeterStatus> Status(string? status) => status switch
    {
        "ok" => MeterStatus.Ok,
        "failed" => MeterStatus.Failed,
        "replaced" => MeterStatus.Replaced,
        _ => StatusInvalid,
    };

    public static string StatusCode(MeterStatus status) => status switch
    {
        MeterStatus.Ok => "ok",
        MeterStatus.Failed => "failed",
        MeterStatus.Replaced => "replaced",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>The unit a meter of this kind counts in (units engine codes).</summary>
    public static string UnitCode(MeterKind kind) => kind switch
    {
        MeterKind.Hours => "h",
        MeterKind.Km => "km",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
