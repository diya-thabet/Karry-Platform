using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Identity.Domain;

public readonly record struct RoleId(Guid Value) : IEntityId<RoleId>
{
    public static RoleId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>What a role contains, as written to the ledger (codes only; before/after of every change, US-P1-IAM-05 AC2).</summary>
public sealed record RoleContent(IReadOnlyList<string> Permissions, IReadOnlyDictionary<string, string> FieldLevels)
{
    public static RoleContent Of(IEnumerable<string> permissions, IReadOnlyDictionary<string, FieldAccess> fields) =>
        new([.. permissions.Order(StringComparer.Ordinal)],
            fields.OrderBy(f => f.Key, StringComparer.Ordinal).ToDictionary(f => f.Key, f => f.Value.ToString().ToLowerInvariant(), StringComparer.Ordinal));
}

[EventType("identity.role_created.v1")]
[AuditSubject("role", nameof(Role))]
public sealed record RoleCreated(string Role, string? BasedOn, bool Office, RoleContent Content, string? Actor) : DomainEvent;

[EventType("identity.role_changed.v1")]
[AuditSubject("role", nameof(Role))]
public sealed record RoleChanged(string Role, RoleContent Before, RoleContent After, string? Actor) : DomainEvent;

[EventType("identity.role_deleted.v1")]
[AuditSubject("role", nameof(Role))]
public sealed record RoleDeleted(string Role, RoleContent Before, string? Actor) : DomainEvent;

/// <summary>Field levels of a default role changed (its permissions are fixed by the catalogue).</summary>
[EventType("identity.field_levels_changed.v1")]
[AuditSubject("role", nameof(Role))]
public sealed record FieldLevelsChanged(string Role, IReadOnlyDictionary<string, string> Before, IReadOnlyDictionary<string, string> After, string? Actor) : DomainEvent;

/// <summary>
/// A role a tenant defines (US-P1-IAM-05 AC1), from scratch or by cloning: a set of permissions and a level per
/// sensitive field. Its code is derived from its ID, so renaming it never touches the users who hold it. The
/// application checks permissions and fields against the module catalogue; here, the shape rules.
/// </summary>
public sealed class CustomRole : AggregateRoot<RoleId>, ITenantOwned
{
    public const string CodePrefix = "custom-";
    public const int NameMaxLength = 60;
    public const int DescriptionMaxLength = 200;

    private CustomRole(RoleId id, TenantId tenantId, string code, string name, string? description, string? basedOn, bool office,
        IReadOnlyList<string> permissions, IReadOnlyDictionary<string, FieldAccess> fieldLevels, DateTimeOffset createdAt)
        : base(id)
    {
        TenantId = tenantId;
        Code = code;
        Name = name;
        Description = description;
        BasedOn = basedOn;
        Office = office;
        Permissions = permissions;
        FieldLevels = fieldLevels;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public TenantId TenantId { get; private set; }

    /// <summary><c>custom-</c> + the ID: what users' role assignments name.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    /// <summary>The role it was cloned from, for the record (it does not follow later changes of that role).</summary>
    public string? BasedOn { get; private set; }

    /// <summary>Holders sign in to the portal (an invited account); otherwise a field role (PIN on a tablet).</summary>
    public bool Office { get; private set; }

    public IReadOnlyList<string> Permissions { get; private set; }

    public IReadOnlyDictionary<string, FieldAccess> FieldLevels { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public RoleContent Content => RoleContent.Of(Permissions, FieldLevels);

    public static bool IsCustomCode(string? code) => code is not null && code.StartsWith(CodePrefix, StringComparison.Ordinal);

    public static Result<CustomRole> Create(
        TimeProvider clock, TenantId tenant, string? name, string? description, string? basedOn, bool office,
        IReadOnlyCollection<string> permissions, IReadOnlyDictionary<string, FieldAccess> fieldLevels, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(fieldLevels);
        var texts = Texts(name, description);
        if (!texts.IsSuccess)
        {
            return texts.Error;
        }

        var id = EntityId.New<RoleId>(clock);
        var role = new CustomRole(id, tenant, CodePrefix + id.Value.ToString("N"), texts.Value.Name, texts.Value.Description, basedOn, office,
            [.. permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], Copy(fieldLevels), clock.GetUtcNow());
        role.Raise(new RoleCreated(role.Code, basedOn, office, role.Content, actor) { OccurredAt = role.CreatedAt });
        return role;
    }

    /// <summary>Replaces name, description, permissions and field levels; false (and no event) when nothing changed.</summary>
    public Result<bool> Change(
        TimeProvider clock, string? name, string? description, IReadOnlyCollection<string> permissions, IReadOnlyDictionary<string, FieldAccess> fieldLevels, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(fieldLevels);
        var texts = Texts(name, description);
        if (!texts.IsSuccess)
        {
            return texts.Error;
        }

        var before = Content;
        var nextPermissions = permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var nextFields = Copy(fieldLevels);
        var after = RoleContent.Of(nextPermissions, nextFields);
        var renamed = texts.Value.Name != Name || texts.Value.Description != Description;
        var contentChanged = !before.Permissions.SequenceEqual(after.Permissions) || !Same(before.FieldLevels, after.FieldLevels);
        if (!renamed && !contentChanged)
        {
            return false;
        }

        (Name, Description, Permissions, FieldLevels, UpdatedAt) = (texts.Value.Name, texts.Value.Description, nextPermissions, nextFields, clock.GetUtcNow());
        if (contentChanged)
        {
            Raise(new RoleChanged(Code, before, after, actor) { OccurredAt = UpdatedAt });
        }

        return true;
    }

    /// <summary>Records the deletion (the application refuses it while users hold the role).</summary>
    public void Delete(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Raise(new RoleDeleted(Code, Content, actor) { OccurredAt = clock.GetUtcNow() });
    }

    internal static bool Same(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static Dictionary<string, FieldAccess> Copy(IReadOnlyDictionary<string, FieldAccess> fields) =>
        fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);

    private static Result<(string Name, string? Description)> Texts(string? name, string? description)
    {
        var cleanName = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (cleanName.Length is 0 or > NameMaxLength || cleanName.Any(char.IsControl))
        {
            return Error.Validation("identity.role_name_invalid", $"A role needs a name of 1–{NameMaxLength} characters.");
        }

        var cleanDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (cleanDescription is { Length: > DescriptionMaxLength })
        {
            return Error.Validation("identity.role_description_invalid", $"The description is at most {DescriptionMaxLength} characters.");
        }

        return (cleanName, cleanDescription);
    }
}

public readonly record struct DefaultRoleSettingsId(Guid Value) : IEntityId<DefaultRoleSettingsId>
{
    public static DefaultRoleSettingsId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// A tenant's field levels for one default role (US-P1-IAM-07 AC1): the permissions of a default role are fixed by
/// the catalogue, its field levels are company policy. Only the overridden fields are stored.
/// </summary>
public sealed class DefaultRoleSettings : AggregateRoot<DefaultRoleSettingsId>, ITenantOwned
{
    private DefaultRoleSettings(DefaultRoleSettingsId id, TenantId tenantId, string role)
        : base(id)
    {
        TenantId = tenantId;
        Role = role;
        FieldLevels = new Dictionary<string, FieldAccess>(StringComparer.Ordinal);
    }

    public TenantId TenantId { get; private set; }

    public string Role { get; private set; }

    public IReadOnlyDictionary<string, FieldAccess> FieldLevels { get; private set; }

    public static DefaultRoleSettings For(TimeProvider clock, TenantId tenant, string role)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!Roles.Defaults.ContainsKey(role))
        {
            throw new ArgumentException($"'{role}' is not a default role.", nameof(role));
        }

        return new DefaultRoleSettings(EntityId.New<DefaultRoleSettingsId>(clock), tenant, role);
    }

    /// <summary>
    /// Sets the overrides; <paramref name="before"/> and <paramref name="after"/> are the effective levels (catalogue
    /// defaults applied) for the ledger. False when nothing changed.
    /// </summary>
    public bool Change(TimeProvider clock, IReadOnlyDictionary<string, FieldAccess> overrides,
        IReadOnlyDictionary<string, FieldAccess> before, IReadOnlyDictionary<string, FieldAccess> after, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(overrides);
        var was = RoleContent.Of([], before).FieldLevels;
        var now = RoleContent.Of([], after).FieldLevels;
        FieldLevels = overrides.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        if (CustomRole.Same(was, now))
        {
            return false;
        }

        Raise(new FieldLevelsChanged(Role, was, now, actor) { OccurredAt = clock.GetUtcNow() });
        return true;
    }
}
