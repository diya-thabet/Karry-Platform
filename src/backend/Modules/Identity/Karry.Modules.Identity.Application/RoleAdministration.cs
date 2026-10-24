using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Identity.Application;

/// <summary>A new custom role, from scratch or cloned from <see cref="BasedOn"/> (whose content fills what is left out).</summary>
public sealed record CreateRole(
    string? Name, string? Description, string? BasedOn, bool? Office, IReadOnlyList<string>? Permissions, IReadOnlyDictionary<string, string>? FieldLevels);

/// <summary>Replaces a custom role's name, description, permissions and field levels (a field left out gets its fallback).</summary>
public sealed record ChangeRole(string? Name, string? Description, IReadOnlyList<string>? Permissions, IReadOnlyDictionary<string, string>? FieldLevels);

/// <summary>Replaces a role's field levels (a field left out gets the catalogue default of a default role, its fallback otherwise).</summary>
public sealed record ChangeFieldLevels(IReadOnlyDictionary<string, string>? FieldLevels);

/// <summary>
/// Custom roles and field levels (US-P1-IAM-05, US-P1-IAM-07 AC1). The endpoints require <c>identity.roles.manage</c>;
/// here, the rules:
/// <list type="bullet">
/// <item>default roles keep the permissions the modules declare (clone them to change them); their field levels are company policy;</item>
/// <item>only permissions and fields the modules declare; no privileged permission the caller lacks tenant-wide (threat T-13);</item>
/// <item>a privileged change (a privileged permission added or removed, a field raised to read or write, a privileged
/// role deleted) needs a sign-in of the last 5 minutes (AC3);</item>
/// <item>a role someone holds cannot be deleted; every change is in the ledger with before and after (AC2).</item>
/// </list>
/// </summary>
public sealed class RoleAdministration(
    IRoleStore store, RoleDirectory directory, ICallerAccess caller, ITenantContext tenant, IAccessCache cache, MfaSync mfa, TimeProvider clock)
{
    public static readonly Error NotFound = Error.NotFound("identity.role_not_found", "No such role in this company.");

    public async Task<Result<RoleView>> CreateAsync(CreateRole command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var roles = await directory.LoadAsync(cancellationToken).ConfigureAwait(false);
        RoleView? basis = null;
        if (command.BasedOn is { } basedOn && (basis = roles.Find(basedOn)) is null)
        {
            return Error.Validation("identity.role_unknown", "The role to copy does not exist.");
        }

        var permissions = command.Permissions ?? (IReadOnlyCollection<string>?)basis?.Permissions ?? [];
        var levels = Levels(command.FieldLevels, basis?.FieldLevels);
        if (!levels.IsSuccess)
        {
            return levels.Error;
        }

        if (Check(permissions, levels.Value) is { } invalid)
        {
            return invalid;
        }

        var after = Effective(permissions, levels.Value);
        if ((Escalation(permissions) ?? StepUpFor(Empty, after)) is { } refused)
        {
            return refused;
        }

        var created = CustomRole.Create(clock, Tenant, command.Name, command.Description, basis?.Code, command.Office ?? basis?.Office ?? true,
            permissions, levels.Value, caller.Subject);
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        store.Add(created.Value);
        return await SavedAsync(created.Value.Code, roles, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<RoleView>> ChangeAsync(string code, ChangeRole command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var roles = await directory.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (roles.Find(code) is not { } current)
        {
            return NotFound;
        }

        if (current.IsDefault)
        {
            return Error.BusinessRule("identity.default_role_readonly", "A default role's permissions cannot change; copy it to make your own.");
        }

        var permissions = command.Permissions ?? [];
        var levels = Levels(command.FieldLevels, null);
        if (!levels.IsSuccess)
        {
            return levels.Error;
        }

        if (Check(permissions, levels.Value) is { } invalid)
        {
            return invalid;
        }

        // Only permissions it gains count for escalation: anyone allowed to manage roles may remove rights.
        var added = permissions.Where(p => !current.Permissions.Contains(p)).ToList();
        if ((Escalation(added) ?? StepUpFor(current, Effective(permissions, levels.Value))) is { } refused)
        {
            return refused;
        }

        var role = (await store.CustomRolesAsync(cancellationToken).ConfigureAwait(false)).Single(r => r.Code == code);
        var changed = role.Change(clock, command.Name, command.Description, permissions, levels.Value, caller.Subject);
        if (!changed.IsSuccess)
        {
            return changed.Error;
        }

        return changed.Value ? await SavedAsync(code, roles, cancellationToken).ConfigureAwait(false) : current;
    }

    public async Task<Result<RoleView>> ChangeFieldLevelsAsync(string code, ChangeFieldLevels command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var roles = await directory.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (roles.Find(code) is not { } current)
        {
            return NotFound;
        }

        var catalog = directory.Catalog;
        var defaults = current.IsDefault
            ? TenantRoles.EffectiveDefaultLevels(catalog, code, null)
            : TenantRoles.EffectiveCustomLevels(catalog, new Dictionary<string, FieldAccess>());
        var levels = Levels(command.FieldLevels, null);
        if (!levels.IsSuccess)
        {
            return levels.Error;
        }

        if (Check([], levels.Value) is { } invalid)
        {
            return invalid;
        }

        var after = defaults.ToDictionary(f => f.Key, f => levels.Value.GetValueOrDefault(f.Key, f.Value), StringComparer.Ordinal);
        if (StepUpFor(current, current with { FieldLevels = after }) is { } refused)
        {
            return refused;
        }

        bool changed;
        if (current.IsDefault)
        {
            var settings = (await store.DefaultRoleSettingsAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(s => s.Role == code);
            if (settings is null)
            {
                settings = DefaultRoleSettings.For(clock, Tenant, code);
                store.Add(settings);
            }

            // Only what differs from the catalogue is stored: a later change of a catalogue default still applies elsewhere.
            var overrides = after.Where(f => f.Value != defaults[f.Key]).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
            changed = settings.Change(clock, overrides, current.FieldLevels, after, caller.Subject);
        }
        else
        {
            var role = (await store.CustomRolesAsync(cancellationToken).ConfigureAwait(false)).Single(r => r.Code == code);
            var result = role.Change(clock, role.Name, role.Description, role.Permissions, levels.Value, caller.Subject);
            if (!result.IsSuccess)
            {
                return result.Error;
            }

            changed = result.Value;
        }

        return changed ? await SavedAsync(code, roles, cancellationToken).ConfigureAwait(false) : current;
    }

    public async Task<Result<bool>> DeleteAsync(string code, CancellationToken cancellationToken)
    {
        var roles = await directory.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (roles.Find(code) is not { } current)
        {
            return NotFound;
        }

        if (current.IsDefault)
        {
            return Error.BusinessRule("identity.default_role_undeletable", "Default roles cannot be deleted.");
        }

        if (current.Privileged && StepUp.Check(caller, clock) is { } refused)
        {
            return refused;
        }

        if (await store.IsHeldAsync(code, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict("identity.role_in_use", "Some users hold this role: give them another role first.");
        }

        var role = (await store.CustomRolesAsync(cancellationToken).ConfigureAwait(false)).Single(r => r.Code == code);
        role.Delete(clock, caller.Subject);
        store.Remove(role);
        var saved = await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        if (saved.IsSuccess)
        {
            cache.ForgetTenant(Tenant);
        }

        return saved;
    }

    private static readonly RoleView Empty = new(string.Empty, false, null, null, null, false, false,
        new HashSet<string>(), new Dictionary<string, FieldAccess>());

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Roles need a tenant.");

    /// <summary>
    /// Saves, forgets cached access and, when the role became privileged or stopped being so, brings its holders'
    /// second factor in step (a newly privileged role ends their sessions).
    /// </summary>
    private async Task<Result<RoleView>> SavedAsync(string code, TenantRoles before, CancellationToken cancellationToken)
    {
        var saved = await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            return saved.Error;
        }

        cache.ForgetTenant(Tenant);
        var after = await directory.LoadAsync(cancellationToken).ConfigureAwait(false);
        var role = after.Find(code)!;
        if (before.Find(code) is { } previous && previous.Privileged != role.Privileged)
        {
            foreach (var holder in await store.AccountHoldersAsync(code, cancellationToken).ConfigureAwait(false))
            {
                await mfa.ApplyAsync(holder, holder.RequiresMfa(before.Lookup), holder.RequiresMfa(after.Lookup), retry: false, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return role;
    }

    /// <summary>Parsed levels (<c>hidden</c>, <c>masked</c>, <c>read</c>, <c>write</c>); left out: <paramref name="basis"/>.</summary>
    private static Result<IReadOnlyDictionary<string, FieldAccess>> Levels(
        IReadOnlyDictionary<string, string>? given, IReadOnlyDictionary<string, FieldAccess>? basis)
    {
        if (given is null)
        {
            return Result.Success<IReadOnlyDictionary<string, FieldAccess>>(new Dictionary<string, FieldAccess>(basis ?? new Dictionary<string, FieldAccess>(), StringComparer.Ordinal));
        }

        var levels = new Dictionary<string, FieldAccess>(StringComparer.Ordinal);
        foreach (var (field, text) in given)
        {
            if (!Enum.TryParse<FieldAccess>(text, ignoreCase: true, out var level) || !Enum.IsDefined(level) || int.TryParse(text, out _))
            {
                return Error.Validation("identity.field_level_invalid", $"'{text}' is not a field level (hidden, masked, read or write).");
            }

            levels[field] = level;
        }

        return levels;
    }

    private Error? Check(IEnumerable<string> permissions, IReadOnlyDictionary<string, FieldAccess> levels)
    {
        if (permissions.FirstOrDefault(p => directory.Catalog.Permission(p) is null) is { } unknown)
        {
            return Error.Validation("identity.permission_unknown", $"'{unknown}' is not a permission of this platform.");
        }

        return levels.Keys.FirstOrDefault(f => directory.Catalog.SensitiveField(f) is null) is { } field
            ? Error.Validation("identity.field_unknown", $"'{field}' is not a sensitive field of this platform.")
            : null;
    }

    /// <summary>A role may only gain a privileged permission its author holds tenant-wide.</summary>
    private Error? Escalation(IEnumerable<string> permissions) =>
        permissions.Any(p => directory.Catalog.Permission(p)!.Privileged && !caller.Current.ScopeOf(p).TenantWide)
            ? Error.Forbidden("identity.escalation", "You cannot give a role rights you do not have yourself.")
            : null;

    private RoleView Effective(IEnumerable<string> permissions, IReadOnlyDictionary<string, FieldAccess> levels) =>
        Empty with
        {
            Permissions = permissions.ToHashSet(StringComparer.Ordinal),
            FieldLevels = TenantRoles.EffectiveCustomLevels(directory.Catalog, levels),
        };

    /// <summary>Step-up when the change touches a privileged permission or raises a field to read or write.</summary>
    private Error? StepUpFor(RoleView before, RoleView after)
    {
        var catalog = directory.Catalog;
        var permissionChange = before.Permissions.Except(after.Permissions).Concat(after.Permissions.Except(before.Permissions))
            .Any(p => catalog.Permission(p)?.Privileged == true);
        var raised = after.FieldLevels.Any(f => f.Value >= FieldAccess.Read && f.Value > before.FieldLevels.GetValueOrDefault(f.Key, FieldAccess.Hidden));
        return permissionChange || raised ? StepUp.Check(caller, clock) : null;
    }
}
