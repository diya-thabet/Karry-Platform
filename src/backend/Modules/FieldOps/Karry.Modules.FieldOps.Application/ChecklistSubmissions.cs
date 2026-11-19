using System.Text.Json;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Sync;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Assets.Contracts;
using Karry.Modules.FieldOps.Domain;
using Karry.Modules.Trust.Contracts;
using Karry.SharedKernel.Results;

namespace Karry.Modules.FieldOps.Application;

/// <summary>Persistence port of checklist submissions and the holds they open, in the upload's transaction (ADR-0035 §3, §4).</summary>
public interface IChecklistSubmissionStore
{
    /// <summary>Joins the upload's transaction; called before the first query.</summary>
    Task JoinAsync(ISyncTransaction transaction, CancellationToken cancellationToken);

    Task<ChecklistTemplate?> TemplateAsync(ChecklistTemplateId id, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid rootId, CancellationToken cancellationToken);

    void Add(ChecklistSubmission submission, SafetyHold? hold);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>A checklist record as signed (ADR-0035 §3); the operator is the envelope's signer, never a record field.</summary>
public sealed record ChecklistRecord(Guid RootId, Guid TemplateId, int TemplateVersion, Guid AssetId, DateTimeOffset SubmittedAt, IReadOnlyList<ChecklistAnswer> Answers);

/// <summary>
/// <c>fieldops.checklist.submit.v1</c> (US-P2-FO-02, ADR-0035 §3, §4): an operator's signed pre-start checklist. Stored
/// once, with its envelope, in the upload's transaction; a critical Not OK opens a hold on the machine in the same
/// transaction (ledgered <c>fieldops.asset_marked_unsafe.v1</c>) and comes back flagged <c>ASSET_UNSAFE</c>.
/// </summary>
public sealed class SubmitChecklist(IChecklistSubmissionStore store, IEnvelopeRecorder envelopes, IAssetDirectory assets, ITenantContext tenant, TimeProvider clock)
    : ISyncCommandHandler
{
    public const string Name = "fieldops.checklist.submit.v1";

    /// <summary>The record's shape is wrong, or its answers do not fit the template version it names.</summary>
    public static readonly HandlerOutcome.Rejected Invalid = new("fieldops.checklist_invalid", Retryable: false);

    public static readonly HandlerOutcome.Rejected Exists = new("fieldops.checklist_exists", Retryable: false);

    public static readonly HandlerOutcome.Rejected AssetUnknown = new("fieldops.asset_unknown", Retryable: false);

    private static readonly HashSet<string> Required = ["rootId", "version", "templateId", "templateVersion", "assetId", "submittedAt", "answers"];

    private static readonly HashSet<string> AnswerRequired = ["item", "answer"];

    private static readonly HashSet<string> AnswerOptional = ["photo"];

    private static readonly HashSet<string> NoOptional = [];

    public string Type => Name;

    /// <summary>The checklist record when it has exactly the shape of ADR-0035 §3, else null (a final <c>fieldops.checklist_invalid</c>).</summary>
    public static ChecklistRecord? ReadRecord(JsonElement record)
    {
        if (!RecordFields.HasOnly(record, Required, NoOptional)
            || !RecordFields.Uuid(record, "rootId", out var rootId) || !RecordFields.WholeNumber(record, "version", 1, 1, out _)
            || !RecordFields.Uuid(record, "templateId", out var templateId) || !RecordFields.WholeNumber(record, "templateVersion", 1, int.MaxValue, out var templateVersion)
            || !RecordFields.Uuid(record, "assetId", out var assetId) || !RecordFields.Instant(record, "submittedAt", out var submittedAt)
            || record.GetProperty("answers") is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() is 0 or > ChecklistRules.MaxItems)
        {
            return null;
        }

        var answers = new List<ChecklistAnswer>();
        foreach (var answer in list.EnumerateArray())
        {
            if (!RecordFields.HasOnly(answer, AnswerRequired, AnswerOptional)
                || answer.GetProperty("item") is not { ValueKind: JsonValueKind.String } item || answer.GetProperty("answer") is not { ValueKind: JsonValueKind.String } value)
            {
                return null;
            }

            string? photo = null;
            if (answer.TryGetProperty("photo", out _))
            {
                if (!RecordFields.ContentHash(answer, "photo", out var hash))
                {
                    return null;
                }

                photo = hash;
            }

            answers.Add(new ChecklistAnswer(item.GetString()!, value.GetString()!, photo));
        }

        return new ChecklistRecord(rootId, templateId, templateVersion, assetId, submittedAt, answers);
    }

    public async Task<HandlerOutcome> HandleAsync(VerifiedCommand command, ISyncTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!command.Payload.TryGetProperty("record", out var json) || ReadRecord(json) is not { } record)
        {
            return Invalid;
        }

        var (signed, problem) = SignedRecord.Read(command.Payload, record.RootId, 1, ChecklistRules.RecordType, Invalid);
        if (signed is null)
        {
            return problem!;
        }

        if (await assets.FindAsync(record.AssetId, cancellationToken).ConfigureAwait(false) is not { } asset)
        {
            return AssetUnknown;
        }

        await store.JoinAsync(transaction, cancellationToken).ConfigureAwait(false);
        var template = await store.TemplateAsync(new ChecklistTemplateId(record.TemplateId), cancellationToken).ConfigureAwait(false);
        if (template is null || template.Version != record.TemplateVersion || template.AssetType != asset.Type
            || ChecklistRules.CriticalNotOk(template, record.Answers) is not { } critical)
        {
            return Invalid;
        }

        if (await store.ExistsAsync(record.RootId, cancellationToken).ConfigureAwait(false))
        {
            return Exists;
        }

        var tenantId = tenant.Current ?? throw new InvalidOperationException("A sync command is handled in its tenant.");
        var outcome = await envelopes.RecordAsync(transaction.Connection, transaction.Transaction,
            new EnvelopeSubmission(tenantId.Value, command.DeviceId, command.CommandId, signed.Envelope, signed.DeviceSignature, command.ReceivedAt), cancellationToken).ConfigureAwait(false);
        if (outcome is EnvelopeOutcome.Refused refused)
        {
            return new HandlerOutcome.Rejected(refused.Code, Retryable: false);
        }

        var submission = new ChecklistSubmission
        {
            TenantId = tenantId,
            RootId = record.RootId,
            TemplateId = template.Id,
            AssetType = template.AssetType,
            TemplateVersion = template.Version,
            AssetId = record.AssetId,
            SiteId = command.SiteId,
            OperatorId = signed.Signer,
            SubmittedAt = record.SubmittedAt,
            Answers = record.Answers,
            CriticalItems = critical,
            EnvelopeId = signed.EnvelopeId,
            CommandId = command.CommandId,
            DeviceId = command.DeviceId,
            ReceivedAt = command.ReceivedAt,
        };
        store.Add(submission, critical.Count > 0 ? SafetyHold.Open(clock, submission) : null);
        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return critical.Count == 0 ? new HandlerOutcome.Accepted(record.RootId) : new HandlerOutcome.Conflict(record.RootId, [ShiftEntryFlags.AssetUnsafe]);
    }
}

/// <summary>The holds of the current company (RLS) and their releases.</summary>
public interface ISafetyHoldStore
{
    Task<SafetyHold?> FindAsync(SafetyHoldId id, CancellationToken cancellationToken);

    Task<bool> ReleasedAsync(SafetyHoldId id, CancellationToken cancellationToken);

    /// <summary>Saves the release; false when the hold was released meanwhile.</summary>
    Task<bool> TryAddAsync(SafetyHoldRelease release, CancellationToken cancellationToken);
}

/// <summary>A release as the portal sends it: why the machine may run again.</summary>
public sealed record ReleaseHold(string? Reason);

/// <summary>
/// Releasing a hold (ADR-0035 §5, US-P2-FO-02 AC3): a manager with <c>fieldops.holds.release</c> on the hold's site
/// decides, with a reason of 10 to 500 characters; the decision and its author are ledgered with the release.
/// </summary>
public sealed class SafetyHolds(ISafetyHoldStore store, ICallerAccess caller, TimeProvider clock)
{
    public static readonly Error NotFound = Error.NotFound("fieldops.hold_not_found", "No such hold on your sites.");

    public static readonly Error AlreadyReleased = Error.Conflict("fieldops.hold_released", "This hold is already released.");

    public static readonly Error ReasonInvalid = Error.Validation("fieldops.release_reason_invalid",
        $"Say why the machine may run again, in {SafetyHoldRelease.ReasonMinLength} to {SafetyHoldRelease.ReasonMaxLength} characters.");

    public async Task<Result<SafetyHoldRelease>> ReleaseAsync(Guid holdId, ReleaseHold command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The hold first: outside the caller's sites (or company) it does not exist, whatever the body says.
        var id = new SafetyHoldId(holdId);
        var hold = await store.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (hold is null || !caller.Current.ScopeOf(FieldOpsPermissions.HoldsRelease).CoversSite(hold.SiteId))
        {
            return NotFound;
        }

        var reason = command.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < SafetyHoldRelease.ReasonMinLength or > SafetyHoldRelease.ReasonMaxLength)
        {
            return ReasonInvalid;
        }

        if (await store.ReleasedAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return AlreadyReleased;
        }

        var release = SafetyHoldRelease.Release(clock, hold, reason, caller.Subject);
        return await store.TryAddAsync(release, cancellationToken).ConfigureAwait(false) ? release : AlreadyReleased;
    }
}
