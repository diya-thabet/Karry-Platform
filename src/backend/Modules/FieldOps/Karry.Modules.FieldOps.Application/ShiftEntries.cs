using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Karry.BuildingBlocks.Sync;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Assets.Contracts;
using Karry.Modules.FieldOps.Domain;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Trust.Contracts;

namespace Karry.Modules.FieldOps.Application;

/// <summary>Persistence port of shift entries, in the upload's transaction (ADR-0031 §2).</summary>
public interface IShiftEntryStore
{
    /// <summary>Joins the upload's transaction; called before the first query.</summary>
    Task JoinAsync(ISyncTransaction transaction, CancellationToken cancellationToken);

    /// <summary>The highest version of an entry, or null when the entry is unknown in this company.</summary>
    Task<ShiftEntryVersion?> LatestAsync(Guid rootId, CancellationToken cancellationToken);

    /// <summary>The latest closed version on <paramref name="meterId"/> that ended at or before <paramref name="before"/>, other entries only.</summary>
    Task<ShiftEntryVersion?> LastEndOfMeterAsync(Guid meterId, Guid exceptRootId, DateTimeOffset before, CancellationToken cancellationToken);

    /// <summary>
    /// The current versions of the operator's other entries that may share time with <c>[from, to)</c> (no
    /// <paramref name="to"/>: still running); the caller decides with <see cref="ShiftEntryRules.Overlaps"/>.
    /// </summary>
    Task<IReadOnlyList<ShiftEntryVersion>> SegmentsOfOperatorAsync(Guid operatorId, Guid exceptRootId, DateTimeOffset from, DateTimeOffset? to, CancellationToken cancellationToken);

    void Add(ShiftEntryVersion version);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A signed record as a command carries it (ADR-0034 §5): <c>{record, envelope, deviceSignature}</c>, where the
/// envelope names this record and its version and holds the hash of the record's RFC 8785 form.
/// </summary>
public sealed record SignedRecord(JsonElement Record, JsonElement Envelope, string DeviceSignature)
{
    public static readonly HandlerOutcome.Rejected Invalid = new("fieldops.entry_invalid", Retryable: false);

    public static readonly HandlerOutcome.Rejected EnvelopeMismatch = new("fieldops.envelope_mismatch", Retryable: false);

    /// <summary>The signed record, or a final rejection when the payload or the envelope's binding to the record is wrong.</summary>
    public static (SignedRecord? Signed, HandlerOutcome.Rejected? Problem) Read(JsonElement payload, Guid rootId, int version)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("record", out var record) || !payload.TryGetProperty("envelope", out var envelope)
            || !payload.TryGetProperty("deviceSignature", out var signature) || signature.ValueKind != JsonValueKind.String
            || payload.EnumerateObject().Count() != 3 || envelope.ValueKind != JsonValueKind.Object)
        {
            return (null, Invalid);
        }

        var binds = envelope.TryGetProperty("recordType", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == ShiftEntryRules.RecordType
            && envelope.TryGetProperty("recordId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == rootId.ToString()
            && envelope.TryGetProperty("recordVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n == version
            && envelope.TryGetProperty("payloadHash", out var hash) && hash.ValueKind == JsonValueKind.String && hash.GetString() == SignatureEnvelopes.PayloadHash(record);
        return binds ? (new SignedRecord(record, envelope, signature.GetString()!), null) : (null, EnvelopeMismatch);
    }

    /// <summary>The person who signed (the envelope's signer, the one unlocked on the tablet); read after the envelope was accepted.</summary>
    public Guid Signer => Guid.Parse(Envelope.GetProperty("signer").GetProperty("userId").GetString()!);

    public Guid EnvelopeId => Guid.Parse(Envelope.GetProperty("envelopeId").GetString()!);
}

/// <summary>Reading the members of a record strictly: exact names, exact types, no nulls (ADR-0034 §5).</summary>
public static partial class RecordFields
{
    public static bool HasOnly(JsonElement record, IReadOnlySet<string> required, IReadOnlySet<string> optional) =>
        record.ValueKind == JsonValueKind.Object
        && required.All(name => record.TryGetProperty(name, out _))
        && record.EnumerateObject().All(p => (required.Contains(p.Name) || optional.Contains(p.Name)) && p.Value.ValueKind != JsonValueKind.Null);

    public static bool Uuid(JsonElement record, string name, out Guid value)
    {
        value = Guid.Empty;
        return record.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String && UuidText().IsMatch(e.GetString()!) && Guid.TryParse(e.GetString(), out value);
    }

    public static bool Instant(JsonElement record, string name, out DateTimeOffset value)
    {
        value = default;
        return record.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String && InstantText().IsMatch(e.GetString()!)
            && DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    public static bool MeterValue(JsonElement record, string name, out decimal value)
    {
        value = 0;
        return record.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String && ShiftEntryRules.TryParseMeterValue(e.GetString(), out value);
    }

    public static bool WholeNumber(JsonElement record, string name, int min, int max, out int value)
    {
        value = 0;
        return record.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value) && value >= min && value <= max;
    }

    public static bool ContentHash(JsonElement record, string name, out string value)
    {
        value = string.Empty;
        if (!record.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.String || !HashText().IsMatch(e.GetString()!))
        {
            return false;
        }

        value = e.GetString()!;
        return true;
    }

    /// <summary>An optional text of at most <paramref name="max"/> characters; true when absent.</summary>
    public static bool OptionalText(JsonElement record, string name, int max, out string? value)
    {
        value = null;
        if (!record.TryGetProperty(name, out var e))
        {
            return true;
        }

        value = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        return value is not null && value.Length <= max && value.Trim().Length > 0;
    }

    [GeneratedRegex(@"\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UuidText();

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex InstantText();

    [GeneratedRegex(@"\Asha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HashText();
}

/// <summary>
/// <c>fieldops.shift_entry.start.v1</c> (US-P2-FO-01, ADR-0034): an operator starts a segment on a machine. Stored as
/// version 1 with its envelope in the upload's transaction; the shift is resolved from the site's rules at the start's
/// time; what needs a look is flagged, never refused (ADR-0031 §3).
/// </summary>
public sealed class StartShiftEntry(
    IShiftEntryStore store, IEnvelopeRecorder envelopes, IMeterDirectory meters, IShiftCalendar shifts, ITenantContext tenant) : ISyncCommandHandler
{
    public const string Name = "fieldops.shift_entry.start.v1";

    public static readonly HandlerOutcome.Rejected MeterUnknown = new("fieldops.meter_unknown", Retryable: false);

    public static readonly HandlerOutcome.Rejected EntryExists = new("fieldops.entry_exists", Retryable: false);

    /// <summary>The previous segment of a handover is not here yet (another tablet's upload): retried later.</summary>
    public static readonly HandlerOutcome.Rejected PreviousUnknown = new("fieldops.previous_unknown", Retryable: true);

    private static readonly HashSet<string> Required = ["rootId", "version", "assetId", "meterId", "startedAt", "startValue", "fuelStartPercent", "startPhoto"];

    private static readonly HashSet<string> Optional = ["previousRootId", "remarks"];

    public string Type => Name;

    public async Task<HandlerOutcome> HandleAsync(VerifiedCommand command, ISyncTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!command.Payload.TryGetProperty("record", out var record) || !RecordFields.HasOnly(record, Required, Optional)
            || !RecordFields.Uuid(record, "rootId", out var rootId) || !RecordFields.WholeNumber(record, "version", 1, 1, out _)
            || !RecordFields.Uuid(record, "assetId", out var assetId) || !RecordFields.Uuid(record, "meterId", out var meterId)
            || !RecordFields.Instant(record, "startedAt", out var startedAt) || !RecordFields.MeterValue(record, "startValue", out var startValue)
            || !RecordFields.WholeNumber(record, "fuelStartPercent", 0, 100, out var fuel) || !RecordFields.ContentHash(record, "startPhoto", out var photo)
            || !RecordFields.OptionalText(record, "remarks", ShiftEntryRules.MaxRemarks, out var remarks))
        {
            return SignedRecord.Invalid;
        }

        Guid? previousRootId = null;
        if (record.TryGetProperty("previousRootId", out _))
        {
            if (!RecordFields.Uuid(record, "previousRootId", out var previous) || previous == rootId)
            {
                return SignedRecord.Invalid;
            }

            previousRootId = previous;
        }

        var (signed, problem) = SignedRecord.Read(command.Payload, rootId, 1);
        if (signed is null)
        {
            return problem!;
        }

        if (await meters.FindAsync(meterId, cancellationToken).ConfigureAwait(false) is not { } meter || meter.AssetId != assetId)
        {
            return MeterUnknown;
        }

        await store.JoinAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (await store.LatestAsync(rootId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return EntryExists;
        }

        var segmentNo = 1;
        ShiftEntryVersion? previousEntry = null;
        if (previousRootId is { } previousId)
        {
            previousEntry = await store.LatestAsync(previousId, cancellationToken).ConfigureAwait(false);
            if (previousEntry is null || previousEntry.AssetId != assetId)
            {
                return PreviousUnknown;
            }

            segmentNo = previousEntry.SegmentNo + 1;
        }

        var tenantId = tenant.Current ?? throw new InvalidOperationException("A sync command is handled in its tenant.");
        var outcome = await envelopes.RecordAsync(transaction.Connection, transaction.Transaction,
            new EnvelopeSubmission(tenantId.Value, command.DeviceId, command.CommandId, signed.Envelope, signed.DeviceSignature, command.ReceivedAt), cancellationToken).ConfigureAwait(false);
        if (outcome is EnvelopeOutcome.Refused refused)
        {
            return new HandlerOutcome.Rejected(refused.Code, Retryable: false);
        }

        var shift = await shifts.ResolveAsync(command.SiteId, startedAt, cancellationToken).ConfigureAwait(false);
        var flags = new List<string>(ShiftEntryRules.StartFlags(await ContinuesFromAsync(previousEntry, meterId, rootId, startedAt, cancellationToken).ConfigureAwait(false), startValue));
        if (await OverlapsAsync(store, signed.Signer, rootId, startedAt, null, cancellationToken).ConfigureAwait(false))
        {
            flags.Add(ShiftEntryFlags.Overlap);
        }

        if (meter.SiteAt(startedAt) != command.SiteId)
        {
            flags.Add(ShiftEntryFlags.AssetNotOnSite);
        }

        if (shift is null)
        {
            flags.Add(ShiftEntryFlags.OutsidePattern);
        }

        store.Add(new ShiftEntryVersion
        {
            TenantId = tenantId,
            RootId = rootId,
            Version = 1,
            Status = ShiftEntryStatus.Open,
            SiteId = command.SiteId,
            AssetId = assetId,
            MeterId = meterId,
            MeterKind = meter.Kind,
            OperatorId = signed.Signer,
            SegmentNo = segmentNo,
            PreviousRootId = previousRootId,
            ShiftDate = shift?.Date,
            ShiftCode = shift?.Code,
            ShiftStartsAt = shift?.StartsAt,
            ShiftEndsAt = shift?.EndsAt,
            StartedAt = startedAt,
            StartValue = startValue,
            FuelStartPercent = fuel,
            StartPhoto = photo,
            Remarks = remarks,
            Estimated = meter.Status == "failed",
            Flags = flags,
            EnvelopeId = signed.EnvelopeId,
            CommandId = command.CommandId,
            DeviceId = command.DeviceId,
            ReceivedAt = command.ReceivedAt,
        });
        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return flags.Count == 0 ? new HandlerOutcome.Accepted(rootId) : new HandlerOutcome.Conflict(rootId, flags);
    }

    /// <summary>Whether the operator has another segment sharing time with <c>[from, to)</c> (OVERLAP, TC-P2-FO-011).</summary>
    internal static async Task<bool> OverlapsAsync(IShiftEntryStore store, Guid operatorId, Guid rootId, DateTimeOffset from, DateTimeOffset? to, CancellationToken cancellationToken) =>
        (await store.SegmentsOfOperatorAsync(operatorId, rootId, from, to, cancellationToken).ConfigureAwait(false))
            .Any(other => ShiftEntryRules.Overlaps(from, to, other.StartedAt, other.EndedAt));

    /// <summary>
    /// The end a start should continue (RULE-FO-01): the previous segment's end in a handover (none yet while that
    /// segment is open: P2.7 re-evaluates), otherwise the meter's last end before the start.
    /// </summary>
    private async Task<decimal?> ContinuesFromAsync(ShiftEntryVersion? previous, Guid meterId, Guid rootId, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        if (previous is not null && previous.MeterId == meterId)
        {
            return previous.EndValue;
        }

        return (await store.LastEndOfMeterAsync(meterId, rootId, startedAt, cancellationToken).ConfigureAwait(false))?.EndValue;
    }
}

/// <summary>
/// <c>fieldops.shift_entry.end.v1</c> (US-P2-FO-04): the operator ends the segment. Stored as the next version, the
/// whole entry with the end added; Δ and the end's time are checked and flagged (RULE-FO-02, ADR-0034 §6).
/// </summary>
public sealed class EndShiftEntry(IShiftEntryStore store, IEnvelopeRecorder envelopes, ITenantContext tenant) : ISyncCommandHandler
{
    public const string Name = "fieldops.shift_entry.end.v1";

    /// <summary>The start is not here yet (it may be in a later upload): retried later.</summary>
    public static readonly HandlerOutcome.Rejected EntryUnknown = new("fieldops.entry_unknown", Retryable: true);

    /// <summary>The entry is already ended, or the version is not the next one.</summary>
    public static readonly HandlerOutcome.Rejected VersionConflict = new("fieldops.version_conflict", Retryable: false);

    private static readonly HashSet<string> Required = ["rootId", "version", "endedAt", "endValue", "fuelEndPercent", "endPhoto"];

    private static readonly HashSet<string> Optional = ["stoppages", "remarks"];

    public string Type => Name;

    public async Task<HandlerOutcome> HandleAsync(VerifiedCommand command, ISyncTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!command.Payload.TryGetProperty("record", out var record) || !RecordFields.HasOnly(record, Required, Optional)
            || !RecordFields.Uuid(record, "rootId", out var rootId) || !RecordFields.WholeNumber(record, "version", 2, int.MaxValue, out var version)
            || !RecordFields.Instant(record, "endedAt", out var endedAt) || !RecordFields.MeterValue(record, "endValue", out var endValue)
            || !RecordFields.WholeNumber(record, "fuelEndPercent", 0, 100, out var fuel) || !RecordFields.ContentHash(record, "endPhoto", out var photo)
            || !RecordFields.OptionalText(record, "remarks", ShiftEntryRules.MaxRemarks, out var remarks)
            || !Stoppages(record, out var stoppages))
        {
            return SignedRecord.Invalid;
        }

        var (signed, problem) = SignedRecord.Read(command.Payload, rootId, version);
        if (signed is null)
        {
            return problem!;
        }

        await store.JoinAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (await store.LatestAsync(rootId, cancellationToken).ConfigureAwait(false) is not { } start)
        {
            return EntryUnknown;
        }

        if (start.Status != ShiftEntryStatus.Open || version != start.Version + 1)
        {
            return VersionConflict;
        }

        var tenantId = tenant.Current ?? throw new InvalidOperationException("A sync command is handled in its tenant.");
        var outcome = await envelopes.RecordAsync(transaction.Connection, transaction.Transaction,
            new EnvelopeSubmission(tenantId.Value, command.DeviceId, command.CommandId, signed.Envelope, signed.DeviceSignature, command.ReceivedAt), cancellationToken).ConfigureAwait(false);
        if (outcome is EnvelopeOutcome.Refused refused)
        {
            return new HandlerOutcome.Rejected(refused.Code, Retryable: false);
        }

        var flags = new List<string>(ShiftEntryRules.EndFlags(start.MeterKind, start.StartedAt, start.StartValue, endedAt, endValue));
        if (await StartShiftEntry.OverlapsAsync(store, start.OperatorId, rootId, start.StartedAt, endedAt, cancellationToken).ConfigureAwait(false))
        {
            flags.Add(ShiftEntryFlags.Overlap);
        }

        store.Add(new ShiftEntryVersion
        {
            TenantId = tenantId,
            RootId = rootId,
            Version = version,
            Status = ShiftEntryStatus.Closed,
            SiteId = start.SiteId,
            AssetId = start.AssetId,
            MeterId = start.MeterId,
            MeterKind = start.MeterKind,
            OperatorId = start.OperatorId,
            SegmentNo = start.SegmentNo,
            PreviousRootId = start.PreviousRootId,
            ShiftDate = start.ShiftDate,
            ShiftCode = start.ShiftCode,
            ShiftStartsAt = start.ShiftStartsAt,
            ShiftEndsAt = start.ShiftEndsAt,
            StartedAt = start.StartedAt,
            StartValue = start.StartValue,
            FuelStartPercent = start.FuelStartPercent,
            StartPhoto = start.StartPhoto,
            EndedAt = endedAt,
            EndValue = endValue,
            FuelEndPercent = fuel,
            EndPhoto = photo,
            Stoppages = stoppages,
            Remarks = remarks ?? start.Remarks,
            Estimated = start.Estimated,
            Flags = flags,
            EnvelopeId = signed.EnvelopeId,
            CommandId = command.CommandId,
            DeviceId = command.DeviceId,
            ReceivedAt = command.ReceivedAt,
        });
        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return flags.Count == 0 ? new HandlerOutcome.Accepted(rootId) : new HandlerOutcome.Conflict(rootId, flags);
    }

    private static bool Stoppages(JsonElement record, out IReadOnlyList<Stoppage> stoppages)
    {
        stoppages = [];
        if (!record.TryGetProperty("stoppages", out var list))
        {
            return true;
        }

        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > ShiftEntryRules.MaxStoppages)
        {
            return false;
        }

        var read = new List<Stoppage>();
        foreach (var item in list.EnumerateArray())
        {
            if (!RecordFields.HasOnly(item, new HashSet<string> { "reason", "minutes" }, new HashSet<string>())
                || item.GetProperty("reason").ValueKind != JsonValueKind.String || !ShiftEntryRules.StoppageReasons.Contains(item.GetProperty("reason").GetString()!)
                || !RecordFields.WholeNumber(item, "minutes", 1, 24 * 60, out var minutes))
            {
                return false;
            }

            read.Add(new Stoppage(item.GetProperty("reason").GetString()!, minutes));
        }

        stoppages = read;
        return true;
    }
}
