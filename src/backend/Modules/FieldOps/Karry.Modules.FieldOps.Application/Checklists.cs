using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.FieldOps.Domain;
using Karry.SharedKernel.Results;

namespace Karry.Modules.FieldOps.Application;

/// <summary>The checklist templates of the current company (RLS).</summary>
public interface IChecklistTemplateStore
{
    /// <summary>The newest version of each asset type's checklist, by asset type.</summary>
    Task<IReadOnlyList<ChecklistTemplate>> LatestAsync(CancellationToken cancellationToken);

    /// <summary>Every version of one asset type's checklist, newest first (empty when it has none).</summary>
    Task<IReadOnlyList<ChecklistTemplate>> VersionsAsync(string assetType, CancellationToken cancellationToken);

    /// <summary>Saves a new version; false when someone published the same version first.</summary>
    Task<bool> TryAddAsync(ChecklistTemplate template, CancellationToken cancellationToken);
}

/// <summary>An item as the portal sends it.</summary>
public sealed record ChecklistItemInput(string? Code, IReadOnlyDictionary<string, string>? Texts, bool Critical, bool PhotoOnNotOk);

/// <summary>The next version of an asset type's checklist: the full content, nothing carried over implicitly.</summary>
public sealed record PublishChecklist(bool Required, IReadOnlyList<ChecklistItemInput>? Items);

/// <summary>
/// Checklist templates (US-P2-FO-03, ADR-0035 §2): read the newest versions and the history, publish the next version.
/// Two people publishing at once: one wins, the other gets 409 and publishes again on top of it.
/// </summary>
public sealed class ChecklistTemplates(IChecklistTemplateStore store, ITenantContext tenant, ICallerAccess caller, TimeProvider clock)
{
    public static readonly Error Concurrent = Error.Conflict("fieldops.checklist_version_conflict",
        "Another version of this checklist was published at the same time; reload and publish again.");

    public static readonly Error NotFound = Error.NotFound("fieldops.checklist_not_found", "This asset type has no checklist.");

    public Task<IReadOnlyList<ChecklistTemplate>> LatestAsync(CancellationToken cancellationToken) => store.LatestAsync(cancellationToken);

    public async Task<Result<IReadOnlyList<ChecklistTemplate>>> VersionsAsync(string assetType, CancellationToken cancellationToken)
    {
        var versions = await store.VersionsAsync(assetType, cancellationToken).ConfigureAwait(false);
        return versions.Count == 0 ? NotFound : Result.Success(versions);
    }

    public async Task<Result<ChecklistTemplate>> PublishAsync(string assetType, PublishChecklist command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var items = ChecklistRules.Validate(assetType, command.Items?.Select(i => (i.Code, i.Texts, i.Critical, i.PhotoOnNotOk)).ToList());
        if (!items.IsSuccess)
        {
            return items.Error;
        }

        var versions = await store.VersionsAsync(assetType, cancellationToken).ConfigureAwait(false);
        var company = tenant.Current ?? throw new InvalidOperationException("Checklists need a tenant.");
        var template = ChecklistTemplate.Publish(clock, company, assetType, versions.Count > 0 ? versions[0].Version + 1 : 1, command.Required, items.Value, caller.Subject);
        return await store.TryAddAsync(template, cancellationToken).ConfigureAwait(false) ? template : Concurrent;
    }
}
