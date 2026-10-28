using System.Buffers.Text;
using FluentValidation;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Domain;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Results;
using Microsoft.Extensions.Options;

namespace Karry.Modules.Identity.Application;

/// <summary>The tablets of the tenant and their enrollment codes (US-P1-OFF-02, ADR-0028).</summary>
public interface IDeviceStore
{
    void Add(DeviceEnrollment enrollment);

    void Add(Device device);

    Task<DeviceEnrollment?> FindEnrollmentAsync(string codeHash, CancellationToken cancellationToken);

    Task<Device?> FindDeviceAsync(DeviceId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Device>> ListDevicesAsync(CancellationToken cancellationToken);

    /// <summary>The user with this employee ID as typed (trimmed): exact match first, else the only case-insensitive one.</summary>
    Task<UserAccount?> FindByEmployeeIdAsync(string employeeId, CancellationToken cancellationToken);

    /// <summary>Saves; a concurrent change (the same code used twice at once) is a conflict, not an exception.</summary>
    Task<Result<bool>> SaveAsync(CancellationToken cancellationToken);
}

/// <summary>How tablets behave (US-P1-OFF-04 AC2/AC3), sent to each tablet with its settings.</summary>
public sealed record DeviceSettings(int AutoLockMinutes, int OfflineSessionHours);

public sealed record CreateEnrollment(Guid SiteId);

/// <summary>The code (only ever shown here) and the address a tablet opens to enrol with it.</summary>
public sealed record EnrollmentCreated(Guid EnrollmentId, Guid SiteId, string Code, Uri Url, DateTimeOffset ExpiresAt);

/// <summary>The public half of the tablet's P-256 key, as a JWK (<c>kty</c> EC, <c>crv</c> P-256, base64url points).</summary>
public sealed record DevicePublicKey(string? Kty, string? Crv, string? X, string? Y);

public sealed record EnrollDevice(
    string? Tenant, string? Code, DevicePublicKey? PublicKey, string? Proof, string? Name, string? Model, string? Platform, string? AppVersion);

/// <summary>What a tablet keeps after enrolling: who it is, for which company and site, and its PIN key (base64, once).</summary>
public sealed record DeviceEnrolledResponse(Guid DeviceId, Guid TenantId, Guid SiteId, string PinKey, DeviceSettings Settings, DateTimeOffset ServerTime);

public sealed record UnlockOnTablet(string? EmployeeId, string? Pin);

public sealed record TabletUnlocked(Guid UserId, string Name, DateTimeOffset ServerTime);

/// <summary>The calling tablet as the API sees it (status, site, settings, server time for its clock).</summary>
public sealed record DeviceSelf(Guid DeviceId, Guid SiteId, string Status, DeviceSettings Settings, DateTimeOffset ServerTime);

public sealed class EnrollDeviceValidator : AbstractValidator<EnrollDevice>
{
    public EnrollDeviceValidator()
    {
        RuleFor(c => c.Tenant).NotEmpty().MaximumLength(63);
        RuleFor(c => c.Code).NotEmpty().MaximumLength(40);
        RuleFor(c => c.PublicKey).NotNull();
        RuleFor(c => c.Proof).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Name).NotEmpty().MaximumLength(Device.NameMaxLength + 20);
        RuleFor(c => c.Model).MaximumLength(200);
        RuleFor(c => c.Platform).MaximumLength(200);
        RuleFor(c => c.AppVersion).MaximumLength(60);
    }
}

public sealed class UnlockOnTabletValidator : AbstractValidator<UnlockOnTablet>
{
    public UnlockOnTabletValidator()
    {
        RuleFor(c => c.EmployeeId).NotEmpty().MaximumLength(UserAccount.EmployeeIdMaxLength + 8);
        RuleFor(c => c.Pin).NotEmpty().MaximumLength(Pin.Length + 4);
    }
}

/// <summary>
/// Enrollment codes, the device list and revocation, for people with <c>identity.devices.manage</c> on the tablet's
/// site (Tenant Admin everywhere, Site Manager on their sites).
/// </summary>
public sealed class DeviceAdministration(
    IDeviceStore store, ISiteDirectory sites, ITenantDirectory tenants, ITenantContext tenant, ICallerAccess caller,
    IOptions<IdentityOptions> options, TimeProvider clock)
{
    public static readonly Error NotFound = Error.NotFound("devices.not_found", "No such tablet in this company.");

    private ScopeSet Scope => caller.Current.ScopeOf(IdentityPermissions.DevicesManage);

    public async Task<Result<EnrollmentCreated>> CreateEnrollmentAsync(CreateEnrollment command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var site = command.SiteId == Guid.Empty ? null : await sites.FindAsync(command.SiteId, cancellationToken).ConfigureAwait(false);
        if (site is null || site.Archived)
        {
            return Error.Validation("devices.site_invalid", "Choose an active site of the company.");
        }

        if (!Scope.CoversSite(site.Id))
        {
            return Error.Forbidden("identity.scope_exceeded", "You can only enrol tablets on the sites you manage.");
        }

        var current = tenant.Current ?? throw new InvalidOperationException("No tenant.");
        var company = await tenants.FindAsync(current, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The current tenant is unknown.");
        var (enrollment, code) = DeviceEnrollment.Create(clock, current, site.Id, caller.Subject);
        store.Add(enrollment);
        var saved = await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            return saved.Error;
        }

        // The code travels in the fragment: never sent to a server, never in an access log.
        var url = new Uri(options.Value.FieldFor(company.Slug), $"/enroll#{code}");
        return new EnrollmentCreated(enrollment.Id.Value, site.Id, code, url, enrollment.ExpiresAt);
    }

    /// <summary>The tablets of the sites the caller manages, newest first.</summary>
    public async Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken)
    {
        var scope = Scope;
        return [.. (await store.ListDevicesAsync(cancellationToken).ConfigureAwait(false))
            .Where(d => scope.CoversSite(d.SiteId))
            .OrderByDescending(d => d.EnrolledAt)];
    }

    public async Task<Result<Device>> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var device = id == Guid.Empty ? null : await store.FindDeviceAsync(new DeviceId(id), cancellationToken).ConfigureAwait(false);
        if (device is null || !Scope.CoversSite(device.SiteId))
        {
            return NotFound;
        }

        var revoked = device.Revoke(clock, caller.Subject);
        if (!revoked.IsSuccess)
        {
            return revoked.Error;
        }

        var saved = await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? device : saved.Error;
    }
}

/// <summary>
/// The enrollment itself (anonymous: the tablet has no credential yet). The tablet names its company by the slug of
/// its address; the code is the secret, looked up under that company's row-level security only.
/// </summary>
public sealed class EnrollDeviceHandler(IDeviceStore store, ITenantSlugLookup slugs, ITenantContext tenant, IOptions<IdentityOptions> options, TimeProvider clock)
{
    public static readonly Error Unknown = Error.NotFound("devices.enrollment_unknown", "This enrollment code is not valid. Check it or create a new one.");

    public async Task<Result<DeviceEnrolledResponse>> HandleAsync(EnrollDevice command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalized = EnrollmentCode.Normalize(command.Code);
        var resolution = string.IsNullOrWhiteSpace(command.Tenant)
            ? SlugResolution.NotATenantAddress
            : await slugs.ResolveAsync(command.Tenant.Trim().ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
        if (normalized is null || resolution.Tenant is not { } owner)
        {
            return Unknown;
        }

        tenant.Set(owner);
        var enrollment = await store.FindEnrollmentAsync(EnrollmentCode.Hash(normalized), cancellationToken).ConfigureAwait(false);
        if (enrollment is null)
        {
            return Unknown;
        }

        if (command.PublicKey is not { Kty: "EC", Crv: "P-256" } key || Decode(key.X) is not { } x || Decode(key.Y) is not { } y || Decode(command.Proof) is not { } proof)
        {
            return Error.Validation("devices.key_invalid", "The tablet's key or its proof is not valid.");
        }

        var device = Device.Enrol(clock, enrollment, command.Code!, x, y, proof, command.Name, command.Model, command.Platform, command.AppVersion);
        if (!device.IsSuccess)
        {
            return device.Error;
        }

        store.Add(device.Value);
        var saved = await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            return DeviceEnrollment.Expired; // the same code used by another tablet at the same moment
        }

        return new DeviceEnrolledResponse(
            device.Value.Id.Value, owner.Value, device.Value.SiteId, Convert.ToBase64String(device.Value.PinKey), options.Value.DeviceSettings, clock.GetUtcNow());
    }

    private static byte[]? Decode(string? base64Url)
    {
        if (string.IsNullOrEmpty(base64Url))
        {
            return null;
        }

        try
        {
            return Base64Url.DecodeFromChars(base64Url);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// What an enrolled tablet asks for itself: its own record, and the online unlock of a field user (ADR-0028 §4) that
/// opens that user's 72-hour offline window on this tablet.
/// </summary>
public sealed class TabletSession(IDeviceStore store, IOptions<IdentityOptions> options, TimeProvider clock)
{
    public static readonly Error Refused = Error.Unauthorized("identity.unlock_refused", "This person cannot unlock a tablet. Check the employee ID or ask for a PIN.");

    public async Task<Result<DeviceSelf>> SelfAsync(DeviceCaller caller, CancellationToken cancellationToken)
    {
        var device = await store.FindDeviceAsync(new DeviceId(caller.DeviceId), cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return DeviceAdministration.NotFound;
        }

        if (device.Seen(clock))
        {
            await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        return new DeviceSelf(device.Id.Value, device.SiteId, device.Status.ToString().ToLowerInvariant(), options.Value.DeviceSettings, clock.GetUtcNow());
    }

    public async Task<Result<TabletUnlocked>> UnlockAsync(DeviceCaller caller, UnlockOnTablet command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = await store.FindByEmployeeIdAsync(command.EmployeeId?.Trim() ?? string.Empty, cancellationToken).ConfigureAwait(false);

        // Only users who work on this tablet's site (or company-wide) unlock it.
        if (user is null || !user.Roles.Any(r => r.SiteId is null || r.SiteId == caller.SiteId))
        {
            return Refused;
        }

        var unlocked = user.UnlockWithPin(clock, command.Pin, caller.DeviceId);
        var saved = await store.SaveAsync(cancellationToken).ConfigureAwait(false); // failures and locks count even when refused
        if (!unlocked.IsSuccess)
        {
            return unlocked.Error;
        }

        return saved.IsSuccess ? new TabletUnlocked(user.Id.Value, user.DisplayName, clock.GetUtcNow()) : saved.Error;
    }
}
