using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Karry.BuildingBlocks.Sync;
using Karry.Modules.Assets.Contracts;
using Karry.Modules.FieldOps.Application;
using Karry.Modules.FieldOps.Domain;
using Karry.Modules.Integrity.Contracts;
using Karry.Modules.Trust.Contracts;
using Karry.Modules.Verification.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.FieldOps.Infrastructure;

/// <summary>
/// What Verification asks about shift entries (ADR-0037 §1, §4): whether a person drove a machine during the time an
/// entry covers (a segment runs as for OVERLAP, <see cref="ShiftEntryRules.RunsUntil"/>), and a controller's correction
/// applied as a new version. Row-level security keeps both in the company.
/// </summary>
internal sealed class ShiftEntryReviewFacts(
    FieldOpsDbContext db, IShiftEntryStore store, IMeterEvents meterEvents, IIntegrityFlags integrity, TimeProvider clock) : IReviewableRecords
{
    /// <summary>The fields a controller may correct (US-P2-VER-04 AC1): the meter values and the fuel levels.</summary>
    public static readonly IReadOnlyList<string> Correctable = ["startValue", "endValue", "fuelStartPercent", "fuelEndPercent"];

    /// <summary>A corrected Δ beyond this many hours of the signed one needs a second approver (US-P2-VER-04 AC3).</summary>
    public const decimal FourEyesHours = 2m;

    /// <summary>… or beyond this share of the signed Δ.</summary>
    public const decimal FourEyesShare = 0.05m;

    public string RecordType => ReviewRecordTypes.ShiftEntry;

    public async Task<bool> OperatedAsync(Guid person, Guid assetId, DateTimeOffset from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        // A segment that started more than a run before the window cannot reach into it; one starting after it cannot either.
        var earliest = from - ShiftEntryRules.MaxOpenRun - ShiftEntryRules.MaxOpenRun;
        var latest = to ?? from + ShiftEntryRules.MaxOpenRun;
        var versions = await db.ShiftEntries.AsNoTracking()
            .Where(e => e.OperatorId == person && e.AssetId == assetId && e.StartedAt >= earliest && e.StartedAt < latest)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return versions
            .GroupBy(e => e.RootId)
            .Select(g => g.MaxBy(e => e.Version)!)
            .Any(e => ShiftEntryRules.Overlaps(e.StartedAt, ShiftEntryRules.RunsUntil(e), from, to));
    }

    public async Task<CorrectionOutcome> CorrectAsync(
        DbConnection connection, DbTransaction transaction, Guid recordId, int version, IReadOnlyDictionary<string, string> fields, string reason, string? actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var joined = new Joined(connection, transaction);
        await store.JoinAsync(joined, cancellationToken).ConfigureAwait(false);
        var latest = await store.LatestAsync(recordId, cancellationToken).ConfigureAwait(false);
        if (latest is null || latest.Version != version)
        {
            return new CorrectionOutcome.Moved();
        }

        if (fields.Count == 0 || fields.Keys.Any(f => !Correctable.Contains(f)))
        {
            return new CorrectionOutcome.Refused("verification.field_not_correctable", $"Only {string.Join(", ", Correctable)} are corrected.");
        }

        var startValue = latest.StartValue;
        var endValue = latest.EndValue;
        var fuelStart = latest.FuelStartPercent;
        var fuelEnd = latest.FuelEndPercent;
        foreach (var (field, text) in fields)
        {
            switch (field)
            {
                case "startValue" when ShiftEntryRules.TryParseMeterValue(text, out var value):
                    startValue = value;
                    break;
                case "endValue" when latest.EndValue is not null && ShiftEntryRules.TryParseMeterValue(text, out var value):
                    endValue = value;
                    break;
                case "fuelStartPercent" when int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) && percent <= 100:
                    fuelStart = percent;
                    break;
                case "fuelEndPercent" when latest.FuelEndPercent is not null && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) && percent <= 100:
                    fuelEnd = percent;
                    break;
                default:
                    return new CorrectionOutcome.Refused("verification.value_invalid", $"{field}: {text} is not a value this entry can take.");
            }
        }

        var changes = new List<FieldChange>();
        void Changed(string field, string? before, string? after)
        {
            if (before != after)
            {
                changes.Add(new FieldChange(field, before, after));
            }
        }

        Changed("startValue", Meter(latest.StartValue), Meter(startValue));
        Changed("endValue", Meter(latest.EndValue), Meter(endValue));
        Changed("fuelStartPercent", Whole(latest.FuelStartPercent), Whole(fuelStart));
        Changed("fuelEndPercent", Whole(latest.FuelEndPercent), Whole(fuelEnd));
        if (changes.Count == 0)
        {
            return new CorrectionOutcome.Refused("verification.nothing_corrected", "The values are the ones already signed.");
        }

        var corrected = Copy(latest, version + 1, startValue, endValue, fuelStart, fuelEnd, actor, reason);
        store.Add(corrected);
        await store.SaveAsync(cancellationToken).ConfigureAwait(false);

        // The corrected values are what the meter's continuity and the entry's Δ are judged on from now (ADR-0036).
        await IntegrityFindings.SyncContinuityAsync(store, meterEvents, integrity, joined, corrected.MeterId, corrected.MeterKind, corrected.SiteId, cancellationToken)
            .ConfigureAwait(false);
        if (corrected.EndValue is not null)
        {
            var tolerance = (await integrity.SettingsAsync(IntegrityRules.Delta, cancellationToken).ConfigureAwait(false)).Thresholds["toleranceHours"];
            await integrity.SyncAsync(connection, transaction, new FlagSubject(IntegrityFindings.ShiftEntry, recordId), IntegrityRules.Delta, corrected.SiteId,
                IntegrityFindings.Delta(corrected, tolerance), cancellationToken).ConfigureAwait(false);
        }

        return new CorrectionOutcome.Corrected(corrected.Version, Hash(corrected), changes, Material(latest, corrected));
    }

    /// <summary>
    /// Beyond the four-eyes thresholds (US-P2-VER-04 AC3): the entry's Δ moves by more than <see cref="FourEyesHours"/>
    /// on an hour meter, or by more than <see cref="FourEyesShare"/> of the signed Δ on any meter.
    /// </summary>
    public static bool Material(ShiftEntryVersion signed, ShiftEntryVersion corrected)
    {
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(corrected);
        if (signed.EndValue is not { } signedEnd || corrected.EndValue is not { } correctedEnd)
        {
            return signed.StartValue != corrected.StartValue && Math.Abs(signed.StartValue - corrected.StartValue) > (signed.MeterKind == "hours" ? FourEyesHours : 0m);
        }

        var signedDelta = signedEnd - signed.StartValue;
        var moved = Math.Abs(correctedEnd - corrected.StartValue - signedDelta);
        return (signed.MeterKind == "hours" && moved > FourEyesHours) || moved > Math.Abs(signedDelta) * FourEyesShare;
    }

    private ShiftEntryVersion Copy(
        ShiftEntryVersion v, int version, decimal startValue, decimal? endValue, int fuelStart, int? fuelEnd, string? actor, string reason) => new()
        {
            TenantId = v.TenantId,
            RootId = v.RootId,
            Version = version,
            Status = v.Status,
            SiteId = v.SiteId,
            AssetId = v.AssetId,
            MeterId = v.MeterId,
            MeterKind = v.MeterKind,
            OperatorId = v.OperatorId,
            SegmentNo = v.SegmentNo,
            PreviousRootId = v.PreviousRootId,
            ShiftDate = v.ShiftDate,
            ShiftCode = v.ShiftCode,
            ShiftStartsAt = v.ShiftStartsAt,
            ShiftEndsAt = v.ShiftEndsAt,
            StartedAt = v.StartedAt,
            StartValue = startValue,
            StartPosition = v.StartPosition,
            FuelStartPercent = fuelStart,
            StartPhoto = v.StartPhoto,
            EndedAt = v.EndedAt,
            EndValue = endValue,
            EndPosition = v.EndPosition,
            FuelEndPercent = fuelEnd,
            EndPhoto = v.EndPhoto,
            Stoppages = v.Stoppages,
            Remarks = v.Remarks,
            Estimated = v.Estimated,
            Flags = v.Flags,
            EnvelopeId = Guid.Empty,
            CommandId = Guid.CreateVersion7(),
            DeviceId = Guid.Empty,
            ReceivedAt = clock.GetUtcNow(),
            CorrectedBy = actor,
            CorrectionReason = reason,
        };

    /// <summary>The corrected version's hash (no device signed it): RFC 8785 of its values, as the ledger keeps it.</summary>
    private static string Hash(ShiftEntryVersion v)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rootId"] = v.RootId.ToString("D"),
            ["version"] = v.Version,
            ["startedAt"] = v.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["startValue"] = Meter(v.StartValue),
            ["fuelStartPercent"] = v.FuelStartPercent,
            ["endedAt"] = v.EndedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["endValue"] = Meter(v.EndValue),
            ["fuelEndPercent"] = v.FuelEndPercent,
            ["correctedBy"] = v.CorrectedBy,
            ["correctionReason"] = v.CorrectionReason,
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(record));
        return SignatureEnvelopes.PayloadHash(document.RootElement);
    }

    private static string? Meter(decimal? value) => value?.ToString("0.0", CultureInfo.InvariantCulture);

    private static string? Whole(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private sealed record Joined(DbConnection Connection, DbTransaction Transaction) : ISyncTransaction;
}
