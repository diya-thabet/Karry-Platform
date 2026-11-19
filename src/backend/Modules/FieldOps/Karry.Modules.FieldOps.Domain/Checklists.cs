using System.Text.RegularExpressions;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.FieldOps.Domain;

public readonly record struct ChecklistTemplateId(Guid Value) : IEntityId<ChecklistTemplateId>
{
    public static ChecklistTemplateId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// One item of a checklist (US-P2-FO-03 AC1): a stable code, its text in the company's languages, whether a Not OK
/// stops the machine (<see cref="Critical"/>) and whether a Not OK needs a photo (always for a critical item).
/// </summary>
public sealed record ChecklistItem(string Code, IReadOnlyDictionary<string, string> Texts, bool Critical, bool PhotoOnNotOk);

[EventType("fieldops.checklist_published.v1")]
[AuditSubject("checklist_template", nameof(AssetType))]
public sealed record ChecklistPublished(string AssetType, int Version, bool Required, IReadOnlyList<ChecklistItem> Items, string? Actor) : DomainEvent;

/// <summary>
/// A published version of the checklist of an asset type (ADR-0035 §2): frozen once published; the next change is the
/// next version. <see cref="Required"/>: the operator must answer it before starting a shift (US-P2-FO-02 AC1).
/// </summary>
public sealed class ChecklistTemplate : AggregateRoot<ChecklistTemplateId>, ITenantOwned
{
    private ChecklistTemplate(ChecklistTemplateId id, TenantId tenantId, string assetType, int version, bool required, IReadOnlyList<ChecklistItem> items, DateTimeOffset at, string? actor)
        : base(id)
    {
        TenantId = tenantId;
        AssetType = assetType;
        Version = version;
        Required = required;
        Items = items;
        PublishedAt = at;
        PublishedBy = actor;
    }

    // EF Core
    private ChecklistTemplate()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    public string AssetType { get; private set; } = null!;

    public int Version { get; private set; }

    public bool Required { get; private set; }

    public IReadOnlyList<ChecklistItem> Items { get; private set; } = [];

    public DateTimeOffset PublishedAt { get; private set; }

    public string? PublishedBy { get; private set; }

    /// <summary>Publishes <paramref name="version"/> (the one after the newest) of the asset type's checklist.</summary>
    public static ChecklistTemplate Publish(TimeProvider clock, TenantId tenant, string assetType, int version, bool required, IReadOnlyList<ChecklistItem> items, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var at = clock.GetUtcNow();
        var template = new ChecklistTemplate(EntityId.New<ChecklistTemplateId>(clock), tenant, assetType, version, required, items, at, actor);
        template.Raise(new ChecklistPublished(assetType, version, required, items, actor) { OccurredAt = at });
        return template;
    }
}

/// <summary>Validation of a checklist (US-P2-FO-03 AC1).</summary>
public static partial class ChecklistRules
{
    public const int MaxItems = 50;
    public const int TextMaxLength = 200;

    /// <summary>The languages a text may be written in (the apps' catalogs, ADR-0030).</summary>
    public static readonly IReadOnlySet<string> Languages = new HashSet<string>(["fr", "en", "ar", "emk", "nqo"], StringComparer.Ordinal);

    /// <summary>
    /// The items as published: 1 to 50, unique codes (letters, digits, dashes), a text of 1 to 200 characters in at least
    /// one supported language, and a photo required on Not OK for every critical item (US-P2-FO-02 AC2).
    /// </summary>
    public static Result<IReadOnlyList<ChecklistItem>> Validate(string? assetType, IReadOnlyList<(string? Code, IReadOnlyDictionary<string, string>? Texts, bool Critical, bool PhotoOnNotOk)>? items)
    {
        if (assetType is null || !TypeCode().IsMatch(assetType))
        {
            return Error.Validation("fieldops.asset_type_invalid", "An asset type code is 2 to 40 lowercase letters, digits or dashes.");
        }

        if (items is null || items.Count is 0 or > MaxItems)
        {
            return Error.Validation("fieldops.checklist_items_invalid", $"A checklist has 1 to {MaxItems} items.");
        }

        var result = new List<ChecklistItem>();
        foreach (var (rawCode, texts, critical, photo) in items)
        {
            var code = rawCode?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!ItemCode().IsMatch(code))
            {
                return Error.Validation("fieldops.checklist_item_code_invalid", "An item code is 1 to 40 lowercase letters, digits or dashes.").With("item", code);
            }

            if (result.Any(i => i.Code == code))
            {
                return Error.Validation("fieldops.checklist_item_code_duplicate", "Each item of a checklist has its own code.").With("item", code);
            }

            var cleaned = (texts ?? new Dictionary<string, string>())
                .Where(t => t.Value is not null)
                .ToDictionary(t => t.Key, t => t.Value.Trim(), StringComparer.Ordinal);
            if (cleaned.Count == 0 || cleaned.Any(t => !Languages.Contains(t.Key) || t.Value.Length is 0 or > TextMaxLength))
            {
                return Error.Validation("fieldops.checklist_item_text_invalid", $"Each item has a text of 1 to {TextMaxLength} characters in fr, en, ar, emk or nqo.").With("item", code);
            }

            result.Add(new ChecklistItem(code, cleaned, critical, critical || photo));
        }

        return result;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{1,39}$")]
    private static partial Regex TypeCode();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex ItemCode();
}
