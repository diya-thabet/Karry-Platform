using System.Security.Cryptography;
using System.Text;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Identity.Domain;

public readonly record struct DeviceId(Guid Value) : IEntityId<DeviceId>
{
    public static DeviceId From(Guid value) => new(value);
}

public readonly record struct DeviceEnrollmentId(Guid Value) : IEntityId<DeviceEnrollmentId>
{
    public static DeviceEnrollmentId From(Guid value) => new(value);
}

public enum DeviceStatus
{
    Active = 1,
    Revoked = 2,
}

/// <summary>An enrollment code was created for a site (US-P1-OFF-02 AC1). The code itself is never recorded.</summary>
[EventType("identity.device_enrollment_created.v1")]
[AuditSubject("device_enrollment", nameof(EnrollmentId))]
public sealed record DeviceEnrollmentCreated(Guid EnrollmentId, Guid SiteId, DateTimeOffset ExpiresAt, string? Actor) : DomainEvent;

/// <summary>A tablet enrolled with a code: bound to the tenant and the code's site.</summary>
[EventType("identity.device_enrolled.v1")]
[AuditSubject("device", nameof(DeviceId))]
public sealed record DeviceEnrolled(Guid DeviceId, Guid SiteId, Guid EnrollmentId, string Name, string? Model, string? Platform, string? AppVersion) : DomainEvent;

/// <summary>A tablet was revoked (US-P1-OFF-02 AC2): it can no longer call the API; what it uploads later is quarantined.</summary>
[EventType("identity.device_revoked.v1")]
[AuditSubject("device", nameof(DeviceId))]
public sealed record DeviceRevoked(Guid DeviceId, string? Actor) : DomainEvent;

/// <summary>
/// The code a tablet types or scans to enrol (ADR-0028 §2): 80 random bits as 16 Crockford base-32 characters,
/// shown as <c>XXXX-XXXX-XXXX-XXXX</c>. Typing is forgiving: case, dashes and spaces are ignored and the letters
/// people confuse with digits (O, I, L) read as those digits. Only the SHA-256 of the normalized code is stored.
/// </summary>
public static class EnrollmentCode
{
    public const int Length = 16;

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New()
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);
        var bits = new System.Numerics.BigInteger(random, isUnsigned: true, isBigEndian: true);
        var code = new StringBuilder(Length + 3);
        for (var i = Length - 1; i >= 0; i--)
        {
            code.Append(Alphabet[(int)((bits >> (5 * i)) & 31)]);
            if (i is 12 or 8 or 4)
            {
                code.Append('-');
            }
        }

        return code.ToString();
    }

    /// <summary>The code as compared (16 characters of the alphabet), or null when what was typed cannot be one.</summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null)
        {
            return null;
        }

        var code = new StringBuilder(Length);
        foreach (var c in typed.ToUpperInvariant())
        {
            var read = c switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                '-' or ' ' => '\0',
                _ => c,
            };
            if (read == '\0')
            {
                continue;
            }

            if (!Alphabet.Contains(read, StringComparison.Ordinal) || code.Length == Length)
            {
                return null;
            }

            code.Append(read);
        }

        return code.Length == Length ? code.ToString() : null;
    }

    public static string Hash(string normalized) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(normalized)));
}

/// <summary>A code to enrol one tablet on one site: valid 15 minutes, single use (US-P1-OFF-02 AC1, TC-P1-OFF-002).</summary>
public sealed class DeviceEnrollment : AggregateRoot<DeviceEnrollmentId>, ITenantOwned
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public static readonly Error Expired = Error.BusinessRule("devices.enrollment_expired", "This enrollment code has expired or was already used. Create a new one.");

    private DeviceEnrollment(DeviceEnrollmentId id, TenantId tenantId, Guid siteId, string codeHash, DateTimeOffset createdAt, string? createdBy)
        : base(id)
    {
        TenantId = tenantId;
        SiteId = siteId;
        CodeHash = codeHash;
        CreatedAt = createdAt;
        ExpiresAt = createdAt + Lifetime;
        CreatedBy = createdBy;
    }

    public TenantId TenantId { get; private set; }

    public Guid SiteId { get; private set; }

    public string CodeHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public string? CreatedBy { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    public DeviceId? DeviceId { get; private set; }

    public static (DeviceEnrollment Enrollment, string Code) Create(TimeProvider clock, TenantId tenant, Guid siteId, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var code = EnrollmentCode.New();
        var enrollment = new DeviceEnrollment(EntityId.New<DeviceEnrollmentId>(clock), tenant, siteId, EnrollmentCode.Hash(EnrollmentCode.Normalize(code)!), clock.GetUtcNow(), actor);
        enrollment.Raise(new DeviceEnrollmentCreated(enrollment.Id.Value, siteId, enrollment.ExpiresAt, actor) { OccurredAt = enrollment.CreatedAt });
        return (enrollment, code);
    }

    /// <summary>Uses the code for <paramref name="device"/>; refused once used or after 15 minutes.</summary>
    public Result<DeviceEnrollment> Use(TimeProvider clock, DeviceId device)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        if (UsedAt is not null || now >= ExpiresAt)
        {
            return Expired;
        }

        UsedAt = now;
        DeviceId = device;
        return this;
    }
}

/// <summary>
/// An enrolled tablet (US-P1-OFF-02, ADR-0028): bound to a tenant and a site, identified by the public half of a key
/// it cannot export. <see cref="PinKey"/> is the per-tablet key of the offline PIN verifiers (ADR-0028 §4).
/// </summary>
public sealed class Device : AggregateRoot<DeviceId>, ITenantOwned
{
    public const int NameMaxLength = 60;

    public const int DetailMaxLength = 80;

    public const int AppVersionMaxLength = 30;

    /// <summary>"Last seen" is written at most this often (not on every request).</summary>
    public static readonly TimeSpan SeenGranularity = TimeSpan.FromMinutes(1);

    private Device(DeviceId id, TenantId tenantId, Guid siteId, string name, string? model, string? platform, string? appVersion, byte[] publicKeyX, byte[] publicKeyY, byte[] pinKey, DateTimeOffset enrolledAt)
        : base(id)
    {
        TenantId = tenantId;
        SiteId = siteId;
        Name = name;
        Model = model;
        Platform = platform;
        AppVersion = appVersion;
        PublicKeyX = publicKeyX;
        PublicKeyY = publicKeyY;
        PinKey = pinKey;
        EnrolledAt = enrolledAt;
        Status = DeviceStatus.Active;
    }

    public TenantId TenantId { get; private set; }

    public Guid SiteId { get; private set; }

    public string Name { get; private set; }

    public string? Model { get; private set; }

    public string? Platform { get; private set; }

    public string? AppVersion { get; private set; }

    public byte[] PublicKeyX { get; private set; }

    public byte[] PublicKeyY { get; private set; }

    public byte[] PinKey { get; private set; }

    public DeviceStatus Status { get; private set; }

    public DateTimeOffset EnrolledAt { get; private set; }

    public DateTimeOffset? LastSeenAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedBy { get; private set; }

    /// <summary>
    /// Enrols a tablet with a valid code: the public key must be a P-256 point and <paramref name="proof"/> its
    /// signature (IEEE P1363) over <c>karry-enroll:&lt;code as typed&gt;</c>, so the caller holds the private key.
    /// </summary>
    public static Result<Device> Enrol(
        TimeProvider clock, DeviceEnrollment enrollment, string typedCode, byte[] x, byte[] y, byte[] proof,
        string? name, string? model, string? platform, string? appVersion)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(enrollment);
        if (!ProvesKey(x, y, proof, $"karry-enroll:{typedCode}"))
        {
            return Error.Validation("devices.key_invalid", "The tablet's key or its proof is not valid.");
        }

        var cleanName = name?.Trim();
        if (string.IsNullOrEmpty(cleanName) || cleanName.Length > NameMaxLength)
        {
            return Error.Validation("devices.name_invalid", $"Give the tablet a name of at most {NameMaxLength} characters.");
        }

        var device = new Device(
            EntityId.New<DeviceId>(clock), enrollment.TenantId, enrollment.SiteId, cleanName, Clip(model, DetailMaxLength), Clip(platform, DetailMaxLength),
            Clip(appVersion, AppVersionMaxLength), x, y, RandomNumberGenerator.GetBytes(32), clock.GetUtcNow());
        var used = enrollment.Use(clock, device.Id);
        if (!used.IsSuccess)
        {
            return used.Error;
        }

        device.LastSeenAt = device.EnrolledAt;
        device.Raise(new DeviceEnrolled(device.Id.Value, device.SiteId, enrollment.Id.Value, device.Name, device.Model, device.Platform, device.AppVersion) { OccurredAt = device.EnrolledAt });
        return device;
    }

    public Result<Device> Revoke(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == DeviceStatus.Revoked)
        {
            return Error.Conflict("devices.already_revoked", "This tablet is already revoked.");
        }

        Status = DeviceStatus.Revoked;
        RevokedAt = clock.GetUtcNow();
        RevokedBy = actor;
        Raise(new DeviceRevoked(Id.Value, actor) { OccurredAt = RevokedAt.Value });
        return this;
    }

    /// <summary>Records that the tablet called the API; false when the last record is recent enough to skip the write.</summary>
    public bool Seen(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        if (LastSeenAt is { } last && now - last < SeenGranularity)
        {
            return false;
        }

        LastSeenAt = now;
        return true;
    }

    private static bool ProvesKey(byte[] x, byte[] y, byte[] proof, string text)
    {
        if (x is not { Length: 32 } || y is not { Length: 32 } || proof is not { Length: 64 })
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
            return key.VerifyData(Encoding.UTF8.GetBytes(text), proof, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false; // not a point of the curve
        }
    }

    private static string? Clip(string? text, int max) => string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(text.Trim().Length, max)];
}
