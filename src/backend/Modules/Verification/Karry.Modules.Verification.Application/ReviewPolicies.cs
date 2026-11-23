using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Verification.Contracts;
using Karry.Modules.Verification.Domain;
using Karry.SharedKernel.Results;

namespace Karry.Modules.Verification.Application;

/// <summary>The current company's review policies (RLS).</summary>
public interface IReviewPolicyStore
{
    /// <summary>The version in force for <paramref name="recordType"/> at <paramref name="at"/> (the newest effective by then), or the defaults.</summary>
    Task<EffectivePolicy> InForceAsync(string recordType, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>A published version (0: the defaults).</summary>
    Task<EffectivePolicy> VersionAsync(string recordType, int version, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewPolicy>> ListAsync(CancellationToken cancellationToken);

    /// <summary>False when another version of the record type was published first: nothing is saved.</summary>
    Task<bool> TryAddAsync(ReviewPolicy policy, CancellationToken cancellationToken);
}

/// <summary>A policy version to publish (US-P2-VER-05 AC1); <c>EffectiveFrom</c> now when absent.</summary>
public sealed record PublishPolicy(bool AutoApprove, decimal FourEyesHours, decimal FourEyesShare, int SlaHours, string? EscalateTo, DateTimeOffset? EffectiveFrom);

/// <summary>
/// Review policies (US-P2-VER-05, ADR-0037 §5): read by everyone who reads the queue, published by a company
/// administrator with a recent sign-in; every version is ledgered and kept, and items keep the version they started under.
/// </summary>
public sealed class ReviewPolicies(IReviewPolicyStore store, ITenantContext tenant, ICallerAccess caller, TimeProvider clock)
{
    /// <summary>The record types a policy applies to.</summary>
    public static IReadOnlyList<string> RecordTypes { get; } = [ReviewRecordTypes.ShiftEntry];

    public Task<IReadOnlyList<ReviewPolicy>> ListAsync(CancellationToken cancellationToken) => store.ListAsync(cancellationToken);

    public async Task<Result<ReviewPolicy>> PublishAsync(string recordType, PublishPolicy command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!RecordTypes.Contains(recordType))
        {
            return ReviewPolicyRules.UnknownRecordType;
        }

        if (StepUp.Check(caller, clock) is { } stepUp)
        {
            return stepUp;
        }

        var company = tenant.Current ?? throw new InvalidOperationException("Policies belong to a tenant.");
        var versions = (await store.ListAsync(cancellationToken).ConfigureAwait(false)).Where(p => p.RecordType == recordType).ToList();
        var next = versions.Count == 0 ? 1 : versions.Max(p => p.Version) + 1;
        var terms = new PolicyTerms(command.AutoApprove, command.FourEyesHours, command.FourEyesShare, command.SlaHours, command.EscalateTo ?? string.Empty);
        var published = ReviewPolicy.Publish(clock, company, recordType, next, terms, command.EffectiveFrom, caller.Subject);
        if (!published.IsSuccess)
        {
            return published.Error;
        }

        return await store.TryAddAsync(published.Value, cancellationToken).ConfigureAwait(false) ? published.Value : Karry.BuildingBlocks.Persistence.RecordVersions.Conflict;
    }
}

/// <summary>Verification's policy permission (ADR-0037 §5).</summary>
public static class ReviewPolicyPermissions
{
    /// <summary>
    /// Publish a review policy: it decides what is approved without a person and when four eyes are needed, so it is a
    /// company administrator's, privileged, with a recent sign-in (threat T-13).
    /// </summary>
    public const string Manage = "verification.policies.manage";
}
