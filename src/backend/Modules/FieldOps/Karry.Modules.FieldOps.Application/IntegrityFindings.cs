using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Karry.BuildingBlocks.Sync;
using Karry.Modules.Assets.Contracts;
using Karry.Modules.FieldOps.Domain;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Integrity.Contracts;
using Karry.Modules.Organization.Contracts;

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

    /// <summary>A signed fact (its signature envelope): the photo, clock and geofence rules judge each start and end on its own.</summary>
    public const string SignedFact = "signature_envelope";

    /// <summary>
    /// RULE-FO-03 on one signed start or end (<see cref="ClockSkew"/>): the subject is the signed fact, linked to the
    /// entry; the tolerance is the rule's <c>maxSkewMinutes</c>.
    /// </summary>
    public static async Task SyncClockAsync(
        IIntegrityFlags integrity, ISyncTransaction transaction, SignedRecord fact, long offsetMs, DateTimeOffset receivedAt, Guid rootId, string part, Guid siteId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrity);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(fact);
        var settings = await integrity.SettingsAsync(IntegrityRules.ClockSkew, cancellationToken).ConfigureAwait(false);
        var tolerance = TimeSpan.FromMinutes((double)settings.Thresholds["maxSkewMinutes"]);
        var deviceTime = fact.DeviceTime;
        var skew = ClockSkew.Judge(deviceTime, offsetMs, receivedAt, tolerance);
        await integrity.SyncAsync(transaction.Connection, transaction.Transaction, new FlagSubject(SignedFact, fact.EnvelopeId), IntegrityRules.ClockSkew, siteId,
            skew is null ? [] :
            [
                new Finding(part, [new FlagSubject(ShiftEntry, rootId)], [], new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["part"] = part,
                    ["reason"] = skew.Reason,
                    ["minutesAhead"] = skew.MinutesAhead.ToString(CultureInfo.InvariantCulture),
                    ["deviceTime"] = deviceTime.ToString("O", CultureInfo.InvariantCulture),
                    ["receivedAt"] = receivedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                }),
            ],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// RULE-FO-04 on one signed start or end: the tablet's position (the envelope's <c>context.geo</c>) is more than the
    /// rule's <c>marginMetres</c> outside the site's drawn boundary. Nothing is judged without a position (location
    /// refused) or without a boundary; the subject is the signed fact, linked to the entry.
    /// </summary>
    public static async Task SyncGeofenceAsync(
        ISiteGeofence geofence, IIntegrityFlags integrity, ISyncTransaction transaction, SignedRecord fact, Guid rootId, string part, Guid siteId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(geofence);
        ArgumentNullException.ThrowIfNull(integrity);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(fact);
        var findings = new List<Finding>();
        if (fact.Position is { } position
            && await geofence.MetresOutsideAsync(siteId, position.Latitude, position.Longitude, cancellationToken).ConfigureAwait(false) is { } outside)
        {
            var margin = (await integrity.SettingsAsync(IntegrityRules.Geofence, cancellationToken).ConfigureAwait(false)).Thresholds["marginMetres"];
            if (outside > margin)
            {
                findings.Add(new Finding(part, [new FlagSubject(ShiftEntry, rootId)], [], new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["part"] = part,
                    ["metresOutside"] = outside.ToString(CultureInfo.InvariantCulture),
                    ["accuracyMetres"] = position.AccuracyM.ToString(CultureInfo.InvariantCulture),
                    ["marginMetres"] = margin.ToString(CultureInfo.InvariantCulture),
                }));
            }
        }

        await integrity.SyncAsync(transaction.Connection, transaction.Transaction, new FlagSubject(SignedFact, fact.EnvelopeId), IntegrityRules.Geofence, siteId,
            findings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// RULE-FO-09 on one signed start or end (threat R-13, security rule): the envelope's signer is not someone the
    /// site's tablets unlock for (unknown, an office account, inactive, deactivated, or without a role on the site). The
    /// fact is kept; the flag carries why, and when the person was deactivated so a controller can tell a fact signed
    /// before it from one signed after.
    /// </summary>
    public static async Task SyncSignerAsync(
        IFieldSigners signers, IIntegrityFlags integrity, ISyncTransaction transaction, SignedRecord fact, Guid rootId, string part, Guid siteId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signers);
        ArgumentNullException.ThrowIfNull(integrity);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(fact);
        var findings = new List<Finding>();
        if (await signers.RefusalAsync(fact.Signer, siteId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["part"] = part,
                ["reason"] = refusal.Reason,
                ["signer"] = fact.Signer.ToString("D"),
                ["deviceTime"] = fact.DeviceTime.ToString("O", CultureInfo.InvariantCulture),
            };
            if (refusal.DeactivatedAt is { } at)
            {
                details["deactivatedAt"] = at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            }

            findings.Add(new Finding(part, [new FlagSubject(ShiftEntry, rootId)], [], details));
        }

        await integrity.SyncAsync(transaction.Connection, transaction.Transaction, new FlagSubject(SignedFact, fact.EnvelopeId), IntegrityRules.UnknownSigner, siteId,
            findings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// RULE-FO-10 on one start (threat R-14, security rule): the machine type's checklist in force when the shift started
    /// is required, and its operator signed none for the machine in the <see cref="ChecklistRules.ValidMinutes"/> before.
    /// The subject is the start's signed fact; a checklist that arrives after its start closes the flag
    /// (<see cref="SubmitChecklist"/> weighs the starts it covers again).
    /// </summary>
    public static async Task SyncChecklistAsync(
        IShiftEntryStore store, IIntegrityFlags integrity, ISyncTransaction transaction, ShiftEntryVersion start, string assetType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(integrity);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(start);
        var findings = new List<Finding>();
        if (await store.ChecklistInForceAsync(assetType, start.StartedAt, cancellationToken).ConfigureAwait(false) is { Required: true } template
            && !await store.CheckedAsync(start.AssetId, start.OperatorId, start.StartedAt.AddMinutes(-ChecklistRules.ValidMinutes), start.StartedAt, cancellationToken)
                .ConfigureAwait(false))
        {
            findings.Add(new Finding("start", [new FlagSubject(ShiftEntry, start.RootId)], [], new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["part"] = "start",
                ["assetType"] = assetType,
                ["templateVersion"] = template.Version.ToString(CultureInfo.InvariantCulture),
                ["validMinutes"] = ChecklistRules.ValidMinutes.ToString(CultureInfo.InvariantCulture),
            }));
        }

        await integrity.SyncAsync(transaction.Connection, transaction.Transaction, new FlagSubject(SignedFact, start.EnvelopeId), IntegrityRules.MissingChecklist,
            start.SiteId, findings, cancellationToken).ConfigureAwait(false);
    }

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

    /// <summary>
    /// The Δ finding of an ended entry, or none (RULE-FO-02, with the company's <c>toleranceHours</c>): the two values, Δ,
    /// the segment's length and the tolerance.
    /// </summary>
    public static IReadOnlyList<Finding> Delta(ShiftEntryVersion entry, decimal toleranceHours)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var tolerance = TimeSpan.FromTicks((long)(toleranceHours * TimeSpan.TicksPerHour));
        if (entry.EndValue is not { } end || entry.EndedAt is not { } endedAt
            || !ShiftEntryRules.DeltaOutOfRange(entry.MeterKind, entry.StartedAt, entry.StartValue, endedAt, end, tolerance))
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
                    ["toleranceHours"] = toleranceHours.ToString(CultureInfo.InvariantCulture),
                    ["minutes"] = ((long)Math.Floor((endedAt - entry.StartedAt).TotalMinutes)).ToString(CultureInfo.InvariantCulture),
                }),
        ];
    }

    private sealed record Joined(DbConnection Connection, DbTransaction Transaction) : ISyncTransaction;

    private static string Text(decimal value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
