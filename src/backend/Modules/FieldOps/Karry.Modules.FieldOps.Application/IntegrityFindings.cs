using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Karry.BuildingBlocks.Sync;
using Karry.Modules.Assets.Contracts;
using Karry.Modules.FieldOps.Domain;
using Karry.Modules.Integrity.Contracts;

namespace Karry.Modules.FieldOps.Application;

/// <summary>
/// What FieldOps tells the Integrity module (ADR-0036 §3), in the upload's transaction: the continuity of a meter
/// (RULE-FO-01, subject the meter) and the Δ of an entry (RULE-FO-02, subject the entry). The entry's own flags
/// (ADR-0034 §6) are kept as they are; these are the flags that open and close over time.
/// </summary>
public static class IntegrityFindings
{
    public const string ShiftEntry = "shift_entry";

    public const string Meter = "meter";

    /// <summary>A signed fact (its signature envelope): the photo rules judge each start and end on its own.</summary>
    public const string SignedFact = "signature_envelope";

    /// <summary>
    /// The photo rules of one signed start or end (ADR-0036 §1, security rules): RULE-FO-05 when the command carries no
    /// capture record for its photo (the photo did not come from the app's camera), RULE-FO-06 when the same photo is
    /// on another entry, or is both the start and the end photo of this one. The subject is the signed fact, linked to
    /// the entry.
    /// </summary>
    public static async Task SyncPhotoAsync(
        IShiftEntryStore store, IIntegrityFlags integrity, ISyncTransaction transaction, JsonElement payload, Guid envelopeId, Guid rootId, string part, string photo,
        string? startPhoto, Guid siteId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(integrity);
        ArgumentNullException.ThrowIfNull(transaction);
        var subject = new FlagSubject(SignedFact, envelopeId);
        FlagSubject[] entry = [new FlagSubject(ShiftEntry, rootId)];
        var captured = HasCapture(payload, photo);
        await integrity.SyncAsync(transaction.Connection, transaction.Transaction, subject, IntegrityRules.PhotoWithoutCapture, siteId,
            captured ? [] : [new Finding(part, entry, [photo], new Dictionary<string, string>(StringComparer.Ordinal) { ["part"] = part })], cancellationToken).ConfigureAwait(false);

        // Other entries with this photo, and this entry itself when its end photo is its start photo.
        var others = await store.EntriesWithPhotoAsync(photo, rootId, cancellationToken).ConfigureAwait(false);
        var sameAsStart = startPhoto == photo;
        var reused = others.Count > 0 || sameAsStart;
        await integrity.SyncAsync(transaction.Connection, transaction.Transaction, subject, IntegrityRules.PhotoReused, siteId,
            !reused ? [] :
            [
                new Finding(part, [.. entry, .. others.Select(o => new FlagSubject(ShiftEntry, o))], [photo], new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["part"] = part,
                    ["otherEntries"] = others.Count.ToString(CultureInfo.InvariantCulture),
                    ["sameAsStart"] = sameAsStart ? "true" : "false",
                }),
            ],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the command's capture records name the photo (<c>sha256:</c> reference against the records' bare hex).</summary>
    public static bool HasCapture(JsonElement payload, string photo)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(SignedRecord.CapturesMember, out var captures) || captures.ValueKind != JsonValueKind.Array
            || !photo.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        var hex = photo["sha256:".Length..];
        return captures.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("sha256", out var sha)
            && sha.ValueKind == JsonValueKind.String && string.Equals(sha.GetString(), hex, StringComparison.Ordinal));
    }

    /// <summary>
    /// Re-evaluates the meter's continuity over all its segments and events and hands Integrity what holds now: a late
    /// entry opens the gaps with its real neighbours and closes the one it explains (TC-P2-FO-008); a meter event closes
    /// the gap it explains (TC-P2-FO-009). Each finding is on its later entry's site.
    /// </summary>
    public static async Task SyncContinuityAsync(
        IShiftEntryStore store, IMeterEvents events, IIntegrityFlags integrity, ISyncTransaction transaction, Guid meterId, string meterKind, Guid? siteId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(integrity);
        ArgumentNullException.ThrowIfNull(transaction);
        var segments = await store.SegmentsOfMeterAsync(meterId, cancellationToken).ConfigureAwait(false);
        var changes = (await events.OfMeterAsync(meterId, cancellationToken).ConfigureAwait(false)).Select(e => new MeterChange(e.OccurredAt, e.Kind, e.ValueBefore, e.ValueAfter));
        var findings = Continuity.Find(segments.Select(Segment), changes).Select(d => ContinuityFinding(d, meterKind)).ToList();
        await integrity.SyncAsync(transaction.Connection, transaction.Transaction, new FlagSubject(Meter, meterId), IntegrityRules.Continuity, siteId, findings, cancellationToken)
            .ConfigureAwait(false);
    }

    public static MeterSegment Segment(ShiftEntryVersion entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new MeterSegment(entry.RootId, entry.SiteId, entry.StartedAt, entry.StartValue, entry.StartPhoto, entry.EndValue, entry.EndPhoto, entry.Estimated);
    }

    /// <summary>A gap as a finding: both entries linked, both photos as evidence (US-P2-FO-07 AC1, AC3), on the later entry's site.</summary>
    public static Finding ContinuityFinding(Discontinuity gap, string meterKind)
    {
        ArgumentNullException.ThrowIfNull(gap);
        return new Finding(
            gap.Key,
            [new FlagSubject(ShiftEntry, gap.Before.RootId), new FlagSubject(ShiftEntry, gap.After.RootId)],
            [gap.Before.EndPhoto!, gap.After.StartPhoto],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["meterKind"] = meterKind,
                ["expectedValue"] = Text(gap.Expected),
                ["foundValue"] = Text(gap.Found),
                ["difference"] = Text(gap.Difference),
            })
        { SiteId = gap.After.SiteId };
    }

    /// <summary>
    /// Re-evaluates a meter after one of its events was recorded (ADR-0036 §4), in the Assets dispatcher's transaction:
    /// the gap the event explains closes, one it contradicts opens. Idempotent (a redelivery syncs the same findings).
    /// </summary>
    public static async Task SyncContinuityAfterEventAsync(
        IShiftEntryStore store, IMeterEvents events, IMeterDirectory meters, IIntegrityFlags integrity, MeterEventSummary meterEvent, DbConnection connection,
        DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meters);
        ArgumentNullException.ThrowIfNull(meterEvent);
        var joined = new Joined(connection, transaction);
        await store.JoinAsync(joined, cancellationToken).ConfigureAwait(false);
        if (await meters.FindAsync(meterEvent.MeterId, cancellationToken).ConfigureAwait(false) is not { } meter)
        {
            return;
        }

        await SyncContinuityAsync(store, events, integrity, joined, meter.MeterId, meter.Kind, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The Δ finding of an ended entry, or none (RULE-FO-02): the two values, Δ and the segment's length.</summary>
    public static IReadOnlyList<Finding> Delta(ShiftEntryVersion entry, bool flagged)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!flagged || entry.EndValue is not { } end || entry.EndedAt is not { } endedAt)
        {
            return [];
        }

        return
        [
            new Finding(
                "delta",
                [],
                [entry.StartPhoto, entry.EndPhoto!],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["meterKind"] = entry.MeterKind,
                    ["startValue"] = Text(entry.StartValue),
                    ["endValue"] = Text(end),
                    ["delta"] = Text(end - entry.StartValue),
                    ["minutes"] = ((long)Math.Floor((endedAt - entry.StartedAt).TotalMinutes)).ToString(CultureInfo.InvariantCulture),
                }),
        ];
    }

    private sealed record Joined(DbConnection Connection, DbTransaction Transaction) : ISyncTransaction;

    private static string Text(decimal value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
